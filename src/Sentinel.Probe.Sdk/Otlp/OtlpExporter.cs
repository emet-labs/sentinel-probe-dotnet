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

    public OtlpExporter(string sentinelBaseUrl, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(sentinelBaseUrl))
        {
            throw new ArgumentException("Sentinel base URL is required.", nameof(sentinelBaseUrl));
        }
        var root = sentinelBaseUrl.TrimEnd('/');
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
        PostAsync(_tracesUrl, sourceHandle, OtlpTraces.Encode(spans), cancellationToken);

    public Task ExportCompletenessAsync(string sourceHandle, ProducerCompletenessSignal signal, CancellationToken cancellationToken) =>
        PostAsync(_logsUrl, sourceHandle, OtlpLogs.EncodeCompleteness(sourceHandle, signal), cancellationToken);

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private async Task PostAsync(string url, string sourceHandle, byte[] body, CancellationToken cancellationToken)
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
            throw new OtlpExportException("OTLP export failed", ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new OtlpExportException(
                    "OTLP export was rejected: HTTP " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + text);
            }
        }
    }
}
