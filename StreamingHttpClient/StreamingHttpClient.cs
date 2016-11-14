using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StreamingHttpClient
{
    class StreamingHttpClient
    {

        // Prior to 2014 RFC2616 described HTTP/1.1.
        // Post 2014 HTTP1/1 was described by multiple RFCs (7230-7237)


        public struct Response
        {
            public string body;
            public bool success;
            public double elapsedMilliseconds;
        }


        private readonly string _userAgent;
        private readonly int _readTimeout;
        private Uri _host;
        private NetworkStream networkStream;
        private TcpClient tcpClient;

        public StreamingHttpClient(string userAgent = "StreamingHttpClient/0.0.1",
                                   int readTimeout = 1000)
        {
            this._userAgent = userAgent;
            this._readTimeout = readTimeout;
        }


        public bool Connected => tcpClient?.Connected ?? false;

        public bool Disconnect()
        {
            networkStream?.Close();
            tcpClient?.Close();

            return !tcpClient?.Connected ?? false;
        }

        public async Task<bool> Connect(Uri server)
        {
            this._host = server;

            networkStream?.Close();
            tcpClient?.Close();

            tcpClient = new TcpClient { NoDelay = true };
            try
            {
                await tcpClient.ConnectAsync(_host.Host, _host.Port);
            }
            catch (SocketException socketException)
            {
                return false;
            }
            networkStream = tcpClient.GetStream();
            networkStream.ReadTimeout = _readTimeout;

            return tcpClient.Connected;
        }

        public async Task<bool> SendRequest(string request, string path = "/", string contentType = "text/xml")
        {
            return await SendRequest(request, (new CancellationTokenSource()).Token, path, contentType);
        }

        public async Task<bool> SendRequest(string request, CancellationToken cancellationToken, string path = "/", string contentType = "text/xml")
        {
            Console.WriteLine("\n\nPosting: \n" + request + "\n\n");
            var headers = "POST " + path + " HTTP/1.1\r\n" +
                          "Connection: keep-alive\r\n" +
                          "Content-Type: " + contentType + "\r\n" +
                          "Content-Length: " + Encoding.UTF8.GetByteCount(request) + "\r\n" +
                          "Host: " + _host.Host + "\r\n" +
                          "Accept: */*\r\n" +
                          "Accept-Encoding: identity\r\n" +
                          "User-Agent: " + _userAgent + "\r\n\r\n" +
                          request;

            byte[] byteArray = Encoding.UTF8.GetBytes(headers);

            try
            {
                await networkStream.WriteAsync(byteArray, 0, byteArray.Length, cancellationToken);
            }
            catch (Exception e)
            {
                //todo: Better error handling
                return false;
            }
            return true;
        }

        public async Task<Response> GetResponse(int timeout = 1000)
        {
            return await GetResponse(timeout, (new CancellationTokenSource()).Token);
        }

        public async Task<Response> GetResponse(int timeout, CancellationToken cancellationToken)
        {
            Console.WriteLine("\n\nWaiting for Response ...\n");
            bool success = false;                               // Response HTTP code 2xx ?
            bool readingBody = false;                           // StreamReader reached body yet?
            int bytesToRead = 0;                                // Content-Length value
            DateTime beginRead = DateTime.Now;                  // Used for timeout purposes
            //todo: Find better structure to save body in
            StringBuilder stringBuilder = new StringBuilder();  // Used for storing body of response

            networkStream.ReadTimeout = timeout;

            while ((DateTime.Now - beginRead).TotalMilliseconds < timeout && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using (StreamReader streamReader = new StreamReader(networkStream, Encoding.UTF8, true, 1024, true))
                    {
                        while (!streamReader.EndOfStream && !cancellationToken.IsCancellationRequested)
                        {
                            var line = await streamReader.ReadLineAsync();

                            Console.WriteLine(line);

                            // Does our head tell us everything is OK?
                            if (line.ToLower().Contains("http/") && Regex.IsMatch(line, "(2\\d\\d)"))
                                success = true;

                            // How big is our body?
                            if (line.ToLower().Contains("content-length"))
                            {
                                try
                                {
                                    bytesToRead = Int32.Parse(line.Split(':')[1]);
                                }
                                catch (Exception e)
                                {
                                    //todo: Better error reporting
                                    break;
                                }
                            }

                            // Gather up body response
                            if (readingBody) stringBuilder.AppendLine(line);

                            // Detect neck
                            if (line.Equals("")) readingBody = true;

                            // Detect end of body
                            if (readingBody && Encoding.UTF8.GetBytes(stringBuilder.ToString()).Length >= bytesToRead)
                            {
                                break;
                            }
                        }
                    }
                    return new Response()
                    {
                        body = stringBuilder.ToString(),
                        success = success,
                        elapsedMilliseconds = (DateTime.Now - beginRead).TotalMilliseconds
                    };
                }
                catch (Exception e)
                {
                    // Calling EndOfStream on empty StreamReader => Exception!
                }
            }

            return new Response()
            {
                body = stringBuilder.ToString(),
                success = success,
                elapsedMilliseconds = (DateTime.Now - beginRead).TotalMilliseconds
            };
        }
    }
}
