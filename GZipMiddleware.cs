using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Owin;

namespace ServiceFabric.Utils.Middlewares
{
    using AppFunc = Func<IDictionary<string, object>, Task>;

    public sealed class GZipMiddleware
    {
        private readonly AppFunc _next;
        private static int _maximumBodyLength;

        public GZipMiddleware(AppFunc next)
        {
            _next = next;
        }

        public GZipMiddleware(AppFunc next, int maximumBodyLength = 1400)
        {
            _next = next;
            _maximumBodyLength = maximumBodyLength;
        }

        public async Task Invoke(IDictionary<string, object> environment)
        {
            var context = new OwinContext(environment);

            // Verifies that the calling client supports gzip encoding.
            if (!((context.Request.Headers.GetValues("Accept-Encoding") ?? Enumerable.Empty<string>()).Where(
                encoding => (encoding ?? string.Empty).ToLowerInvariant().Contains("gzip"))).Any())
            {
                await _next(environment);
                return;
            }

            // Replaces the response stream by a memory stream
            // and keeps track of the real response stream.
            var body = context.Response.Body;
            context.Response.Body = new MemoryStream();

            try
            {
                await _next(environment);

                // Verifies that the response stream is still a readable and seekable stream.
                if (!context.Response.Body.CanSeek || !context.Response.Body.CanRead)
                {
                    throw new InvalidOperationException("The response stream has been replaced by an unreadable or unseekable stream.");
                }

                // Determines if the response stream meets the length requirements to be gzipped.
                if (context.Response.Body.Length >= _maximumBodyLength)
                {
                    context.Response.Headers["Content-Encoding"] = "gzip";

                    // Determines if chunking can be safely used.
                    if (string.Equals(context.Request.Protocol, "HTTP/1.1", StringComparison.Ordinal))
                    {
                        context.Response.Headers["Transfer-Encoding"] = "chunked";

                        // Opens a new GZip stream pointing directly to the real response stream.
                        using (var gzip = new GZipStream(body, CompressionMode.Compress, leaveOpen: true))
                        {
                            // Rewinds the memory stream and copies it to the GZip stream.
                            context.Response.Body.Seek(0, SeekOrigin.Begin);
                            await context.Response.Body.CopyToAsync(gzip, 81920, context.Request.CallCancelled);
                        }

                        return;
                    }

                    // Opens a new buffer to determine the gzipped response stream length.
                    using (var buffer = new MemoryStream())
                    {
                        // Opens a new GZip stream pointing to the buffer stream.
                        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
                        {
                            // Rewinds the memory stream and copies it to the GZip stream.
                            context.Response.Body.Seek(0, SeekOrigin.Begin);
                            await context.Response.Body.CopyToAsync(gzip, 81920, context.Request.CallCancelled);
                        }

                        // Rewinds the buffer stream and copies it to the real stream.
                        // See http://blogs.msdn.com/b/bclteam/archive/2006/05/10/592551.aspx
                        // to see why the buffer is only read after the GZip stream has been disposed.
                        buffer.Seek(0, SeekOrigin.Begin);
                        context.Response.ContentLength = buffer.Length;
                        await buffer.CopyToAsync(body, 81920, context.Request.CallCancelled);
                    }

                    return;
                }

                // Rewinds the memory stream and copies it to the real response stream.
                context.Response.Body.Seek(0, SeekOrigin.Begin);
                context.Response.ContentLength = context.Response.Body.Length;
                await context.Response.Body.CopyToAsync(body, 81920, context.Request.CallCancelled);
            }

            finally
            {
                // Restores the real stream in the environment dictionary.
                context.Response.Body = body;
            }
        }
    }
}
