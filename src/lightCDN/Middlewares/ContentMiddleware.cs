namespace lightCDN
{
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Net.Http.Headers;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;

    public class ContentMiddleware
    {
        private static readonly string OriginalSite = "http://www.anyandesign.cn";
        private readonly RequestDelegate next;
        private readonly ILogger logger;
        private readonly HttpClient httpClient;
        private readonly string localPath;

        public ContentMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, IHostingEnvironment env)
        {
            this.next = next;
            this.logger = loggerFactory.CreateLogger<ContentMiddleware>();
            httpClient = new HttpClient();
            this.localPath = $"{env.ContentRootPath}{Path.DirectorySeparatorChar}cache";
        }

        public async Task Invoke(HttpContext context)
        {
            string path = context.Request.Path;
            if (path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                await this.next.Invoke(context);
                return;
            }

            var originalPath = $"{OriginalSite}{path}";

            var localFilePath = $"{this.localPath}{path.Replace('/', Path.DirectorySeparatorChar)}";

            HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Head, originalPath);
            var response = await this.httpClient.SendAsync(msg);
            if(response.IsSuccessStatusCode)
            {
                DateTimeOffset? lastModified = response.Content.Headers.LastModified;

                try
                {
                    var localFileLastModified = File.GetLastWriteTimeUtc(localFilePath);

                    if (lastModified.HasValue && localFileLastModified >= lastModified.Value)
                    {
                        if (context.Response.Headers[HeaderNames.LastModified].Count == 0)
                        {
                            context.Response.Headers[HeaderNames.LastModified] = localFileLastModified.ToString("R", CultureInfo.InvariantCulture);
                        }

                        string etag = response.Content.Headers.GetValues("ETag").FirstOrDefault();
                        if (context.Response.Headers[HeaderNames.ETag].Count == 0 && !string.IsNullOrWhiteSpace(etag))
                        {
                            context.Response.Headers[HeaderNames.ETag] = EnsureQuoted(etag);
                        }

                        if (context.Response.Headers[HeaderNames.ContentType].Count == 0 && response.Content.Headers.ContentType != null)
                        {
                            context.Response.Headers[HeaderNames.ContentType] = response.Content.Headers.ContentType.ToString();
                        }

                        if (context.Response.Headers[HeaderNames.CacheControl].Count == 0)
                        {
                            context.Response.Headers[HeaderNames.CacheControl] = "public,max-age=600";
                        }

                        if (IsContentModified(context, localFileLastModified))
                        {
                            using (var fs = File.OpenRead(localFilePath))
                            {
                                await fs.CopyToAsync(
                                    context.Response.Body,
                                    bufferSize: 8192,
                                    cancellationToken: context.RequestAborted);
                            }
                        }
                        else
                        {
                            context.Response.StatusCode = StatusCodes.Status304NotModified;
                        }
                    }
                    else
                    {
                        if (lastModified.HasValue)
                        {
                            CacheToLocal(originalPath, localFilePath);
                        }
                        context.Response.Redirect(originalPath);
                    }
                }
                catch(Exception e)
                {
                    if(!(e is FileNotFoundException))
                    {
                        this.logger.LogError("Could not load {0}. {1}", localFilePath, e);
                    }

                    if (lastModified.HasValue)
                    {
                        CacheToLocal(originalPath, localFilePath);
                    }
                    context.Response.Redirect(originalPath);
                }
            }
            else
            {
                try
                {
                    if (File.Exists(localFilePath))
                    {
                        File.Delete(localFilePath);
                    }
                }
                catch(Exception e)
                {
                    this.logger.LogError("Could not delete {0}. {1}", localFilePath, e);
                }
            }

            await this.next.Invoke(context);
        }

        private bool IsContentModified(HttpContext context, DateTimeOffset? lastModifiedTime)
        {
            DateTimeOffset? ifModifiedSinceTime = null;
            DateTimeOffset parsedDateTime;

            ifModifiedSinceTime = DateTimeOffset.TryParseExact(
                context.Request.Headers[HeaderNames.IfModifiedSince],
                "R",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out parsedDateTime) ? parsedDateTime : (DateTimeOffset?)null;

            return (ifModifiedSinceTime == null ||
                lastModifiedTime == null) ||
                DateTimeOffset.Compare(ifModifiedSinceTime.Value, lastModifiedTime.Value) < 0;
        }

        private void CacheToLocal(string url, string localPath)
        {
            Task.Run(async () =>
            {
                try
                {
                    var response = await this.httpClient.GetAsync(url);
                    DateTimeOffset? lastModified = response.Content.Headers.LastModified;

                    if(!lastModified.HasValue)
                    {
                        return;
                    }

                    using (var fs = File.OpenWrite(localPath))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                    
                    File.SetLastWriteTimeUtc(localPath, lastModified.Value.DateTime);
                }
                catch(Exception e)
                {
                    this.logger.LogError("Could not download {0} to {1}. {2}", url, localPath, e);
                }
            });
        }

        private static string EnsureQuoted(string value)
        {
            if (value == null)
            {
                return null;
            }

            string quotedValue = value;

            if (!quotedValue.EndsWith("\"", StringComparison.OrdinalIgnoreCase))
            {
                quotedValue = "\"" + quotedValue + "\"";
            }

            return quotedValue;
        }
    }
}
