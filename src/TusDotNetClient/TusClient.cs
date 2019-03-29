﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TusDotNetClient
{
    public partial class TusClient
    {
        public Dictionary<string, string> AdditionalHeaders { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IWebProxy Proxy { get; set; }

        public async Task<string> CreateAsync(string url, long uploadLength,
            params (string key, string value)[] metadata)
        {
            var requestUri = new Uri(url);
            var client = new TusHttpClient
            {
                Proxy = Proxy
            };

            var request = new TusHttpRequest(url, RequestMethod.Post, AdditionalHeaders);

            request.AddHeader(TusHeaderNames.UploadLength, uploadLength.ToString());
            request.AddHeader(TusHeaderNames.ContentLength, "0");

            request.AddHeader(TusHeaderNames.UploadMetadata, string.Join(",", metadata
                .Select(md =>
                    $"{md.key.Replace(" ", "").Replace(",", "")} {Convert.ToBase64String(Encoding.UTF8.GetBytes(md.value))}")));

            var response = await client.PerformRequestAsync(request)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Created)
                throw new Exception("CreateFileInServer failed. " + response.ResponseString);

            if (!response.Headers.ContainsKey("Location"))
                throw new Exception("Location Header Missing");

            if (!Uri.TryCreate(response.Headers["Location"], UriKind.RelativeOrAbsolute, out var locationUri))
                throw new Exception("Invalid Location Header");

            if (!locationUri.IsAbsoluteUri)
                locationUri = new Uri(requestUri, locationUri);

            return locationUri.ToString();
        }

        public TusOperation<Unit> UploadAsync(string url, FileInfo file) =>
            UploadAsync(url, new FileStream(file.FullName,
                FileMode.Open, FileAccess.Read, FileShare.Read,
                5 * 1024 * 1024, true));

        public TusOperation<Unit> UploadAsync(
            string url,
            Stream fileStream,
            double chunkSize = 5.0,
            CancellationToken cancellationToken = default) => new TusOperation<Unit>(
            async reportProgress =>
            {
                try
                {
                    var offset = await GetFileOffset(url)
                        .ConfigureAwait(false);

                    var client = new TusHttpClient();
                    SHA1 sha = new SHA1Managed();

                    var uploadChunkSize = (int) Math.Ceiling(chunkSize * 1024.0 * 1024.0); // to MB

                    if (offset == fileStream.Length)
                        reportProgress(fileStream.Length, fileStream.Length);

                    var buffer = new byte[uploadChunkSize];

                    void OnProgress(long written, long total) => 
                        reportProgress(offset + written, fileStream.Length);
                    while (offset < fileStream.Length)
                    {
                        fileStream.Seek(offset, SeekOrigin.Begin);

                        var bytesRead = await fileStream.ReadAsync(buffer, 0, uploadChunkSize);
                        var segment = new ArraySegment<byte>(buffer, 0, bytesRead);
                        var sha1Hash = sha.ComputeHash(buffer, 0, bytesRead);

                        var request = new TusHttpRequest(url, RequestMethod.Patch, AdditionalHeaders, segment,
                            cancellationToken);
                        request.AddHeader(TusHeaderNames.UploadOffset, offset.ToString());
                        request.AddHeader(TusHeaderNames.UploadChecksum, $"sha1 {Convert.ToBase64String(sha1Hash)}");
                        request.AddHeader(TusHeaderNames.ContentType, "application/offset+octet-stream");

                        try
                        {
                            request.UploadProgressed += OnProgress;
                            var response = await client.PerformRequestAsync(request)
                                .ConfigureAwait(false);
                            request.UploadProgressed -= OnProgress;

                            if (response.StatusCode != HttpStatusCode.NoContent)
                            {
                                throw new Exception("WriteFileInServer failed. " + response.ResponseString);
                            }

                            offset = long.Parse(response.Headers[TusHeaderNames.UploadOffset]);

//                            reportProgress(offset, fileStream.Length);
                        }
                        catch (IOException ex)
                        {
                            if (ex.InnerException is SocketException socketException)
                            {
                                if (socketException.SocketErrorCode == SocketError.ConnectionReset)
                                {
                                    // retry by continuing the while loop
                                    // but get new offset from server to prevent Conflict error
                                    offset = await GetFileOffset(url)
                                        .ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }

                    return Unit.Default;
                }
                finally
                {
                    fileStream.Dispose();
                }
            });

        public TusOperation<TusHttpResponse> DownloadAsync(string url, CancellationToken cancellationToken = default) =>
            new TusOperation<TusHttpResponse>(
                async reportProgress =>
                {
                    var client = new TusHttpClient();
                    var request = new TusHttpRequest(
                        url,
                        RequestMethod.Get,
                        AdditionalHeaders,
                        cancelToken: cancellationToken);

                    request.DownloadProgressed += reportProgress;

                    var response = await client.PerformRequestAsync(request)
                        .ConfigureAwait(false);

                    request.DownloadProgressed -= reportProgress;

                    return response;
                });

        public async Task<TusHttpResponse> HeadAsync(string url)
        {
            var client = new TusHttpClient();
            var request = new TusHttpRequest(url, RequestMethod.Head, AdditionalHeaders);

            try
            {
                return await client.PerformRequestAsync(request)
                    .ConfigureAwait(false);
            }
            catch (TusException ex)
            {
                return new TusHttpResponse(ex.StatusCode);
            }
        }

        public async Task<TusServerInfo> GetServerInfo(string url)
        {
            var client = new TusHttpClient();
            var request = new TusHttpRequest(url, RequestMethod.Options, AdditionalHeaders);

            var response = await client.PerformRequestAsync(request)
                .ConfigureAwait(false);

            // Spec says NoContent but tusd gives OK because of browser bugs
            if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.OK)
                throw new Exception("getServerInfo failed. " + response.ResponseString);

            response.Headers.TryGetValue(TusHeaderNames.TusResumable, out var version);
            response.Headers.TryGetValue(TusHeaderNames.TusVersion, out var supportedVersions);
            response.Headers.TryGetValue(TusHeaderNames.TusExtension, out var extensions);
            response.Headers.TryGetValue(TusHeaderNames.TusMaxSize, out var maxSizeString);
            response.Headers.TryGetValue(TusHeaderNames.TusChecksumAlgorithm, out var checksumAlgorithms);
            long.TryParse(maxSizeString, out var maxSize);
            return new TusServerInfo(version, supportedVersions, extensions, maxSize, checksumAlgorithms);
        }

        public async Task<bool> Delete(string url)
        {
            var client = new TusHttpClient();
            var request = new TusHttpRequest(url, RequestMethod.Delete, AdditionalHeaders);

            var response = await client.PerformRequestAsync(request)
                .ConfigureAwait(false);

            return response.StatusCode == HttpStatusCode.NoContent ||
                   response.StatusCode == HttpStatusCode.NotFound ||
                   response.StatusCode == HttpStatusCode.Gone;
        }

        private async Task<long> GetFileOffset(string url)
        {
            var client = new TusHttpClient();
            var request = new TusHttpRequest(url, RequestMethod.Head, AdditionalHeaders);

            var response = await client.PerformRequestAsync(request)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.OK)
                throw new Exception("GetFileOffset failed. " + response.ResponseString);

            if (!response.Headers.ContainsKey(TusHeaderNames.UploadOffset))
                throw new Exception("Offset Header Missing");

            return long.Parse(response.Headers[TusHeaderNames.UploadOffset]);
        }
    }
}