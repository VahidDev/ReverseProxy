using HtmlAgilityPack;
using Microsoft.Extensions.Primitives;
using System.IO.Compression;
using System.Text;
using System.Web;

namespace Test.Middleware
{
    public class ReverseProxyMiddleware
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly RequestDelegate _nextMiddleware;

        public ReverseProxyMiddleware(RequestDelegate nextMiddleware)
        {
            _nextMiddleware = nextMiddleware;
        }

        public async Task Invoke(HttpContext context)
        {
            Uri targetUri = BuildTargetUri(context.Request);

            if (targetUri != null)
            {
                HttpRequestMessage targetRequestMessage = CreateTargetMessage(context, targetUri);

                using (HttpResponseMessage responseMessage = await _httpClient
                        .SendAsync(targetRequestMessage, HttpCompletionOption.ResponseContentRead, context.RequestAborted))
                {
                    context.Response.StatusCode = (int)responseMessage.StatusCode;
                    CopyFromTargetResponseHeaders(context, responseMessage);
                    try
                    {
                        responseMessage.Content.Headers
                            .TryGetValues("Content-Encoding",out IEnumerable<string> encodingHeader);

                        byte[] byteArr = await responseMessage.Content.ReadAsByteArrayAsync();
                        byte[] decompressedData = Decompress(byteArr, encodingHeader);
                        string deCompressedString = Encoding.UTF8.GetString(decompressedData);

                        HtmlDocument html = new();
                        html.LoadHtml(deCompressedString);
                        
                        
                        html.OptionOutputOriginalCase = true;

                        string script = "<script defer>\r\n " +
                            "window.onload = function() {\r\n" +
                            "(function () {\r\n  " +
                            "var origOpen = XMLHttpRequest.prototype.open;\r\n  " +
                            "XMLHttpRequest.prototype.open = function (method,url) {\r\n    " +
                            "console.log(url);" +
                            " url = \"https://localhost:44385/customProxy?url=\" + decodeURI(url);" +
                            "console.log(url);" +
                            "console.log(method);" +
                            "console.log(\"request started!\");\r\n    " +
                            "this.addEventListener(\"load\", function () {\r\n      " +
                            "console.log(\"request completed!\");\r\n      " +
                            "});" +
                            "origOpen.apply(this, arguments);\r\n  " +
                            "};\r\n})();" +
                            "\r\n};" +
                            "</script>";
                         
                        html.DocumentNode.AppendChild(html.CreateElement(script));

                        IEnumerable<HtmlNode> anchorTags = html.DocumentNode.Descendants("a");

                        foreach (HtmlNode anchor in anchorTags)
                        {
                            string href = anchor.GetAttributeValue("href", string.Empty);
                            string encodedUrl = HttpUtility.UrlEncode(Encoding.UTF8.GetBytes(href));

                            string newHref = "/customProxy?url=" + encodedUrl;
                            anchor.SetAttributeValue("href", newHref);
                        }

                        IEnumerable<HtmlNode> imageTags = html.DocumentNode.Descendants("img");

                        foreach (HtmlNode image in imageTags)
                        {
                            //string src = anchor.GetAttributeValue("src", string.Empty);
                            //anchor.SetAttributeValue("src", "/customProxy" + src); 
                            //anchor.SetAttributeValue("srcset", "/customProxy" + src);
                            string src = image.GetAttributeValue("src", string.Empty);
                            image.SetAttributeValue("src", "https://www.google.com" + src);
                            image.SetAttributeValue("srcset", "https://www.google.com" + src);
                        }

                        Stream savedHtml = new MemoryStream();
                        html.Save(savedHtml);

                        byte[] dataToCompress = ((MemoryStream)savedHtml).ToArray();
                        byte[] compressedData = Compress(dataToCompress, encodingHeader);
                        string compressedString = Convert.ToBase64String(compressedData);

                        Stream stream = new MemoryStream(compressedData);
                        await stream.CopyToAsync(context.Response.Body);
                    }
                    catch (Exception e)
                    {
                        throw;
                    }
                }
                return;
            }
            await _nextMiddleware(context);
        }

        public static byte[] Compress(byte[] bytes, IEnumerable<string> encodingHeaders)
        {
            using (var memoryStream = new MemoryStream())
            {
                Stream compressedStream = null;
                if (encodingHeaders.Contains("gzip"))
                {
                    compressedStream = new GZipStream(memoryStream, CompressionLevel.Optimal);
                } else if (encodingHeaders.Contains("deflate"))
                {
                    compressedStream = compressedStream = new DeflateStream(memoryStream, CompressionLevel.Optimal);
                }
                else if (encodingHeaders.Contains("br"))
                {
                    compressedStream = new BrotliStream(memoryStream, CompressionLevel.Optimal);
                }
                compressedStream.Write(bytes, 0, bytes.Length);
                compressedStream.Close();
                return memoryStream.ToArray();
            }
        }

        public static byte[] Decompress(byte[] bytes,IEnumerable<string> encodingHeaders)
        {
            using (var memoryStream = new MemoryStream(bytes))
            {
                using (var outputStream = new MemoryStream())
                {
                    Stream decompressedStream = null;
                    if (encodingHeaders.Contains("gzip"))
                    {
                        decompressedStream = new GZipStream(memoryStream, CompressionMode.Decompress);
                    }
                    else if (encodingHeaders.Contains("deflate"))
                    {
                        decompressedStream = decompressedStream = new DeflateStream(memoryStream, CompressionMode.Decompress);
                    }
                    else if (encodingHeaders.Contains("br"))
                    {
                        decompressedStream = new BrotliStream(memoryStream, CompressionMode.Decompress);
                    }

                    decompressedStream.CopyTo(outputStream);
                    decompressedStream.Close();

                    return outputStream.ToArray();
                }
            }
        }

        private HttpRequestMessage CreateTargetMessage(HttpContext context, Uri targetUri)
        {
            HttpRequestMessage requestMessage = new();
            CopyFromOriginalRequestContentAndHeaders(context, requestMessage);

            requestMessage.RequestUri = targetUri;
            requestMessage.Headers.Host = targetUri.Host;
            requestMessage.Method = GetMethod(context.Request.Method);

            return requestMessage;
        }

        private void CopyFromOriginalRequestContentAndHeaders(HttpContext context, HttpRequestMessage requestMessage)
        {
            string requestMethod = context.Request.Method;

            if (!HttpMethods.IsGet(requestMethod) &&
              !HttpMethods.IsHead(requestMethod) &&
              !HttpMethods.IsDelete(requestMethod) &&
              !HttpMethods.IsTrace(requestMethod))
            {
                var streamContent = new StreamContent(context.Request.Body);
                requestMessage.Content = streamContent;
            }

            foreach (KeyValuePair<string, StringValues> header in context.Request.Headers)
            {
                if (header.Key != "Cookie")
                {
                    requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }
        }

        private void CopyFromTargetResponseHeaders(HttpContext context, HttpResponseMessage responseMessage)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in responseMessage.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (KeyValuePair<string, IEnumerable<string>> header in responseMessage.Content.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            context.Response.Headers.Remove("transfer-encoding");
        }

        private static HttpMethod GetMethod(string method)
        {
            if (HttpMethods.IsDelete(method)) return HttpMethod.Delete;
            if (HttpMethods.IsGet(method)) return HttpMethod.Get;
            if (HttpMethods.IsHead(method)) return HttpMethod.Head;
            if (HttpMethods.IsOptions(method)) return HttpMethod.Options;
            if (HttpMethods.IsPost(method)) return HttpMethod.Post;
            if (HttpMethods.IsPut(method)) return HttpMethod.Put;
            if (HttpMethods.IsTrace(method)) return HttpMethod.Trace;
            return new HttpMethod(method);
        }

        private Uri BuildTargetUri(HttpRequest request)
        {
            Uri targetUri = null;
            
            if (request.Path.StartsWithSegments("/customProxy"))
            {
                string remainingPath = string.Empty;
                if (!string.IsNullOrEmpty(request.QueryString.Value))
                {
                    string path = request.QueryString.Value.Substring(5);
                    remainingPath = HttpUtility.UrlDecode(path);
                }

                targetUri = new Uri(remainingPath);
            }
            return targetUri;
        }
    }
}
