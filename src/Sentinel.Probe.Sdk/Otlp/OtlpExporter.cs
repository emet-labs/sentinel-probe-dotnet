using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;

namespace Sentinel.Probe.Sdk.Otlp;

/// <summary>
/// Posts OTLP/HTTP protobuf to Sentinel. Traces go to <c>/v1/traces</c> and
/// completeness goes to <c>/v1/logs</c>. Both carry <c>x-sentinel-source-handle</c>.
/// This is the Probe wire (the ADR-0002 exception in #398). Connect is not called.
/// </summary>
public sealed class OtlpExporter : IDisposable
{
    public const string ProtobufMediaType = "application/x-protobuf";
    public const string SourceHandleHeader = "x-sentinel-source-handle";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _tracesUrl;
    private readonly string _logsUrl;
    private readonly IReadOnlyDictionary<string, string>? _resourceAttributes;

    /// <summary>Longest response body kept on an <see cref="OtlpExportException"/>.</summary>
    public const int MaxErrorBodyChars = 2048;

    /// <param name="sentinelBaseUrl">Sentinel periphery base URL.</param>
    /// <param name="httpClient">Optional client; the exporter does not dispose a supplied one.</param>
    /// <param name="resourceAttributes">
    /// OTLP resource attributes sent with every trace export (<c>service.name</c> and the
    /// provenance keys of platform/probe-config). Resource only: they never become span attributes,
    /// so the eight-key span contract is unchanged. Keys must be non-empty and unique; values are strings.
    /// </param>
    public OtlpExporter(string sentinelBaseUrl, HttpClient? httpClient = null, IReadOnlyDictionary<string, string>? resourceAttributes = null)
    {
        if (string.IsNullOrWhiteSpace(sentinelBaseUrl))
        {
            throw new ArgumentException("Sentinel base URL is required.", nameof(sentinelBaseUrl));
        }
        var root = sentinelBaseUrl.TrimEnd('/');
        if (resourceAttributes != null)
        {
            foreach (var pair in resourceAttributes)
            {
                if (string.IsNullOrEmpty(pair.Key))
                {
                    throw new ArgumentException("A resource attribute key is empty.", nameof(resourceAttributes));
                }
                if (pair.Value == null)
                {
                    throw new ArgumentException("Resource attribute " + pair.Key + " has a null value.", nameof(resourceAttributes));
                }
            }
            _resourceAttributes = new Dictionary<string, string>(resourceAttributes, StringComparer.Ordinal);
        }
        _tracesUrl = root + "/v1/traces";
        _logsUrl = root + "/v1/logs";
        if (httpClient == null)
        {
            _http = new HttpClient();
            _ownsClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsClient = false;
        }
        _http.DefaultRequestHeaders.ExpectContinue = false;
    }

    public Task ExportSpansAsync(string sourceHandle, IReadOnlyList<SpanExport> spans, CancellationToken cancellationToken) =>
        PostAsync(_tracesUrl, sourceHandle, OtlpTraces.Encode(spans, _resourceAttributes), cancellationToken);

    /// <summary>
    /// Posts the spans and returns the rejected span count from Sentinel's
    /// ExportTraceServiceResponse (<see cref="PartialSuccess"/>). The export succeeded only when it
    /// returns 0. A non-2xx status or an undecodable 2xx body throws <see cref="OtlpExportException"/>.
    /// </summary>
    public async Task<long> ExportSpansCheckedAsync(string sourceHandle, IReadOnlyList<SpanExport> spans, CancellationToken cancellationToken) =>
        (await ExportSpansOutcomeAsync(sourceHandle, spans, cancellationToken).ConfigureAwait(false)).RejectedSpans;

    /// <summary>
    /// <see cref="ExportSpansCheckedAsync"/> that also returns Sentinel's <c>error_message</c>, so a caller can
    /// tell a refused duplicate Event id from another rejection.
    /// </summary>
    public async Task<PartialSuccessRejection> ExportSpansOutcomeAsync(string sourceHandle, IReadOnlyList<SpanExport> spans, CancellationToken cancellationToken)
    {
        var body = await PostAsync(_tracesUrl, sourceHandle, OtlpTraces.Encode(spans, _resourceAttributes), cancellationToken).ConfigureAwait(false);
        try
        {
            return PartialSuccess.Rejection(body);
        }
        catch (FormatException ex)
        {
            throw new OtlpExportException("OTLP export returned an undecodable response", ex);
        }
    }

    public Task ExportCompletenessAsync(string sourceHandle, ProducerCompletenessSignal signal, CancellationToken cancellationToken) =>
        PostAsync(_logsUrl, sourceHandle, OtlpLogs.EncodeCompleteness(sourceHandle, signal), cancellationToken);

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private async Task<byte[]> PostAsync(string url, string sourceHandle, byte[] body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceHandle))
        {
            throw new OtlpExportException("source handle is empty");
        }
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(ProtobufMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation(SourceHandleHeader, sourceHandle);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OtlpExportException("OTLP export failed: " + ex.Message, ex) { IsTransient = true };
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                string text;
                try
                {
                    text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    text = "";
                }
                if (text.Length > MaxErrorBodyChars)
                {
                    text = text.Substring(0, MaxErrorBodyChars) + "...";
                }
                var summary = text.Length == 0 ? "" : ": " + text;
                throw new OtlpExportException(
                    "OTLP export was rejected: HTTP " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + response.ReasonPhrase + summary)
                {
                    StatusCode = code,
                    ResponseBody = text,
                    IsTransient = code == 408 || code == 429 || code >= 500,
                };
            }
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
