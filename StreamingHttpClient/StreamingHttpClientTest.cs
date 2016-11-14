using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;

namespace StreamingHttpClient
{

    class StreamingHttpClientTest
    {
        private static readonly Uri ApiUri      = new Uri("http://localhost:8080/");
        private const string LoginMethod        = "loginSubscriber";
        private const string SubscribeMethod    = "subscribeTeesheetUpdate";
        private const string Username           = "username";
        private const string Password           = "P4s5W0rd!";
        private bool connect = true;
        public static void Main(string[] args)
        {
            StreamingHttpClientTest streamingHttpClientTest = new StreamingHttpClientTest();

            streamingHttpClientTest.StartListener();
            streamingHttpClientTest.StartClient();

            Console.Read();
        }

        //=========================================================================================================
        // Client test below
        //=========================================================================================================

        private async void StartClient()
        {
            Console.WriteLine("\n\n" +
                              "\t\t=========================================\n" +
                              "\t\t*** Starting StreamingHttpClient test ***\n" +
                              "\t\t=========================================\n" +
                              "\n\n");

            // Setup Streaming HTTP Client with timeout of 15 minutes
            var streamingHttpClient = new StreamingHttpClient(readTimeout: 15*60*1000);
            bool status = await streamingHttpClient.Connect(ApiUri);

            if (!status)
            {
                Console.WriteLine("[Client] Connecting to " + ApiUri + " failed!");
                return;
            }

            // Login
            var loginRequest = Encoding.UTF8.GetString(CreatePayload(LoginMethod, new[] {Username, Password}));

            Console.WriteLine("[Client] **\tLogging in ... ");
            await streamingHttpClient.SendRequest(loginRequest);
            var loginResponse = await streamingHttpClient.GetResponse();
            if (!loginResponse.success)
            {
                Console.WriteLine("Login FAILED!");
                throw new Exception("Login failed!");
            }
            var _token = ParseLogin(loginResponse.body);

            
            // Subscribe
            var subscribeRequest = Encoding.UTF8.GetString(CreatePayload(SubscribeMethod, new[] {_token}));
            
            Console.WriteLine("[Client] **\tSubscribing ... ");
            await streamingHttpClient.SendRequest(subscribeRequest);
            var subscribeResponse = await streamingHttpClient.GetResponse();
            if (!subscribeResponse.success)
            {
                Console.WriteLine("Subscribe FAILED!");
                throw new Exception("Subscribe failed!");
            }


            // Wait for updates for our tee-times
            while (connect)
            {
                var updateResponse = await streamingHttpClient.GetResponse(1800000);
                if (updateResponse.success)
                {
                    var updateCount = ParseUpdates(updateResponse.body);
                    Console.WriteLine("[Client] Received " + updateCount + " updates in " + updateResponse.elapsedMilliseconds +
                                      "ms.");
                }
                else
                {
                    Console.WriteLine("[Client] Failed getting updates: " + updateResponse.body);
                }
                Thread.Sleep(250);
            }
        }

        private int ParseUpdates(string body)
        {
            try
            {
                XDocument xDocument = XDocument.Parse(body);
                return xDocument.XPathSelectElements("/pushResponse/data/item").Count();
            }
            catch
            {
                return 0;
            }
        }

        private string ParseLogin(string body)
        {
            #region examples

            /* Example GOOD response:
            <?xml version="1.0" encoding="UTF-8"?>

            <methodResponse>
              <params>
                <param>
                  <value>
                    <string>dmlzaXRkdWJhaSwydDE2LTA4LTizIDAxOjEzOjUxLjCzM3wweDAweDAxMTzD</string>
                  </value>
                </param>
              </params>
            </methodResponse>

            <!-- XPATH expression: /methodResponse/params/param/value/string -->
            */

            /* Example BAD response:
            <?xml version="1.0"?>

            <methodResponse>
              <fault>
                <value>
                  <struct>
                    <member>
                      <name>faultCode</name>
                      <value><int>999</int></value>
                    </member>
                    <member>
                      <name>faultString</name>
                      <value><string>Item is not a string type</string></value>
                    </member>
                  </struct>
                </value>
              </fault>
            </methodResponse>
            
            <!-- XPATH expressions:
                /methodResponse/fault/value/struct/member/value/int
                /methodResponse/fault/value/struct/member/value/string
                -->
            */

            #endregion

            try
            {
                XDocument xDocument = XDocument.Parse(body);
                return xDocument.XPathSelectElement("/methodResponse/params/param/value/string").Value;
            }
            //todo: Better error reporting
            catch (Exception e)
            {
                return String.Empty;
            }
        }

        public byte[] CreatePayload(String method, IEnumerable<string> methodParameters)
        {
            XElement paramsXElement = new XElement("params");
            foreach (var methodParameter in methodParameters)
            {
                paramsXElement.Add(new XElement("param",
                                        new XElement("value",
                                            new XElement("string", methodParameter))));
            }

            return Encoding.UTF8.GetBytes(
                new XElement("methodCall",
                    new XElement("methodName", method),
                        paramsXElement).ToString()
                );
        }

        //=========================================================================================================
        //  Server mock below
        //=========================================================================================================

        public bool Subscribed { get; set; }
        public NetworkStream ServerNetworkStream { get; set; }

        private async void StartListener()
        {
            Subscribed = false;
            //NB: This listener can only handle one connection at a time.
            TcpListener tcpListener = TcpListener.Create(8080);
            tcpListener.Start();
            
            var tcpClient = await tcpListener.AcceptTcpClientAsync();

            ServerNetworkStream = tcpClient.GetStream();
            using (StreamReader streamReader = new StreamReader(ServerNetworkStream))
            {
                StringBuilder stringBuilder = new StringBuilder();
                bool bodyReached = false;
                int bytesToRead = 0;
                while (!streamReader.EndOfStream)
                {
                    var line = await streamReader.ReadLineAsync();
                    
                    // New request received
                    if (line.ToLower().Contains("post") || line.ToLower().Contains("get"))
                    {
                        // NB: Resetting here as well, in case previous request didn't complete
                        bodyReached = false;
                        bytesToRead = 0;
                        stringBuilder = new StringBuilder();
                    }

                    // How much of the body should be read before we can expect new request
                    if (line.ToLower().Contains("content-length"))
                    {
                        if (!Int32.TryParse(line.Split(':')[1], out bytesToRead)) bytesToRead = 0;
                        
                    }

                    // Reading body? Collect body request
                    if (bodyReached) stringBuilder.AppendLine(line);
                    // Found neck! Reached body.
                    if (line.Equals("")) bodyReached = true;
                    // If body isn't reached, wait for body. If body.bytes.length < bytesToRead -> keep collecting body
                    if (!bodyReached ||
                        Encoding.UTF8.GetByteCount(stringBuilder.ToString()) < bytesToRead) continue;
                    
                    // Respond to the request
                    byte[] buffer = ApplyHttpHeaders(ServerParseRequest(stringBuilder.ToString()));
                    await ServerNetworkStream.WriteAsync(buffer, 0, buffer.Length);

                    // This request is complete, reset for new request.
                    bodyReached = false;
                    bytesToRead = 0;
                    stringBuilder = new StringBuilder();
                }
            }

        }

        private string ServerParseRequest(string requestPayload)
        {
            try
            {
                XDocument xDocument = XDocument.Parse(requestPayload);
                string methodCall = xDocument.XPathSelectElement("/methodCall/methodName").Value;
                Console.WriteLine("[Server] Received " + methodCall);
                switch (methodCall)
                {
                    case "loginSubscriber":
                        return CreateResponse(Encoding.UTF8.GetBytes("R29sZk5vdywweDAwMDAwRjVD"));
                    case "subscribeTeesheetUpdate":
                        this.Subscribed = true;
                        Task.Factory.StartNew(RandomSendUpdate);

                        return CreateResponse(Encoding.UTF8.GetBytes("Subscription received for " + Username));
                    default:
                        return CreateResponse(Encoding.UTF8.GetBytes("Something went wrong.."));
                }
            }
            catch (Exception e)
            {
                return CreateResponse(Encoding.UTF8.GetBytes(e.Message));
            }
        }

        private async void RandomSendUpdate()
        {
            while (!Subscribed) Thread.Sleep(100);

            while (Subscribed && connect)
            {
                int randomSleep = (new Random()).Next(10000);
                Console.WriteLine("[Server] Sending Updates in " + randomSleep + "ms");
                Thread.Sleep(randomSleep);

                byte[] buffer = ApplyHttpHeaders(CreateUpdateResponse());
                Console.WriteLine("[Server] Sending Updates");
                await ServerNetworkStream.WriteAsync(buffer, 0, buffer.Length);
            }
        }

        private byte[] ApplyHttpHeaders(string payload)
        {
            return Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\n" +
                                          "Connection: keep-alive\r\n" +
                                          "Content-Type: text/xml\r\n" +
                                          "Content-Length: " + Encoding.UTF8.GetByteCount(payload) + "\r\n" +
                                          "Server: StreamingHttpServer\r\n" +
                                          "\r\n" +
                                          payload +
                                          "\r\n");
        }

        private string CreateUpdateResponse()
        {
            int randomItemCount = (new Random()).Next(1000);

            var data = new XElement("data");
            for (int i = 0; i < randomItemCount; i++)
            {
                data.Add(new XElement("item", i));
            }

            return (new XElement("pushResponse", data)).ToString();
        }

        private string CreateResponse(byte[] input)
        {
            return new XElement("methodResponse",
                    new XElement("params",
                        new XElement("param",
                            new XElement("value",
                                new XElement("string", Encoding.UTF8.GetString(input)))))).ToString();
        }
    }
}
