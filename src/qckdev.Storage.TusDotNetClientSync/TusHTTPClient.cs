﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace qckdev.Storage.TusDotNetClientSync
{
    /// <summary>
    /// A class to execute requests against a Tus enabled server.
    /// </summary>
    public class TusHttpClient
    {
        /// <summary>
        /// Get or set the proxy to use for requests.
        /// </summary>
        public IWebProxy Proxy { get; set; }

        /// <summary>
        /// Perform a request to the Tus server.
        /// </summary>
        /// <param name="request">The <see cref="TusHttpRequest"/> to execute.</param>
        /// <returns>A <see cref="TusHttpResponse"/> with the response data.</returns>
        /// <exception cref="TusException">Throws when the request fails.</exception>
        public TusHttpResponse PerformRequest(TusHttpRequest request)
        {
            var segment = request.BodyBytes;

            try
            {
                var webRequest = (HttpWebRequest)WebRequest.Create(request.Url);
                webRequest.AutomaticDecompression = DecompressionMethods.GZip;

                webRequest.Timeout = Timeout.Infinite;
                webRequest.ReadWriteTimeout = Timeout.Infinite;
                webRequest.Method = request.Method;
                webRequest.KeepAlive = false;

                webRequest.Proxy = Proxy;

                try
                {
                    webRequest.ServicePoint.Expect100Continue = false;
                }
                catch (PlatformNotSupportedException)
                {
                    //expected on .net core 2.0 with systemproxy
                    //fixed by https://github.com/dotnet/corefx/commit/a9e01da6f1b3a0dfbc36d17823a2264e2ec47050
                    //should work in .net core 2.2
                }

                //SEND
                var buffer = new byte[4096];

                var totalBytesWritten = 0L;

                webRequest.AllowWriteStreamBuffering = false;
                webRequest.ContentLength = segment.Count;

                foreach (var header in request.Headers)
                    switch (header.Key)
                    {
                        case TusHeaderNames.ContentLength:
                            webRequest.ContentLength = long.Parse(header.Value);
                            break;
                        case TusHeaderNames.ContentType:
                            webRequest.ContentType = header.Value;
                            break;
                        default:
                            webRequest.Headers.Add(header.Key, header.Value);
                            break;
                    }

                if (request.BodyBytes.Count > 0)
                {
                    var inputStream = new MemoryStream(request.BodyBytes.Array, request.BodyBytes.Offset,
                        request.BodyBytes.Count);

                    using (var requestStream = webRequest.GetRequestStream())
                    {
                        inputStream.Seek(0, SeekOrigin.Begin);

                        var bytesWritten = inputStream.Read(buffer, 0, buffer.Length);

                        request.OnUploadProgressed(0, segment.Count);

                        while (bytesWritten > 0)
                        {
                            totalBytesWritten += bytesWritten;

                            request.OnUploadProgressed(totalBytesWritten, segment.Count);

                            requestStream.Write(buffer, 0, bytesWritten);

                            bytesWritten = inputStream.Read(buffer, 0, buffer.Length);
                        }
                    }
                }

                using (var response = (HttpWebResponse)webRequest.GetResponse())
                {

                    //contentLength=0 for gzipped responses due to .net bug
                    long contentLength = Math.Max(response.ContentLength, 0);

                    buffer = new byte[16 * 1024];

                    var outputStream = new MemoryStream();

                    using (var responseStream = response.GetResponseStream())
                    {
                        if (responseStream != null)
                        {
                            var bytesRead = responseStream.Read(buffer, 0, buffer.Length);

                            request.OnDownloadProgressed(0, contentLength);

                            var totalBytesRead = 0L;
                            while (bytesRead > 0)
                            {
                                totalBytesRead += bytesRead;

                                request.OnDownloadProgressed(totalBytesRead, contentLength);

                                outputStream.Write(buffer, 0, bytesRead);

                                bytesRead = responseStream.Read(buffer, 0, buffer.Length);
                            }
                        }
                    }

                    return new TusHttpResponse(
                        response.StatusCode,
                        response.Headers.AllKeys
                            .ToDictionary(headerName => headerName, headerName => response.Headers.Get(headerName)),
                        outputStream.ToArray());
                }
            }
            catch (OperationCanceledException cancelEx)
            {
                throw new TusException(cancelEx);
            }
            catch (WebException ex)
            {
                throw new TusException(ex);
            }
        }
    }
}