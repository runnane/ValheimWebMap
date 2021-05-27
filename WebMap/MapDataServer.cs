using System;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using WebSocketSharp.Net;
using WebSocketSharp.Server;
using UnityEngine;
using static WebMap.WebMapConfig;
using System.Text.RegularExpressions;

namespace WebMap {

    public class WebSocketHandler : WebSocketBehavior {
        public WebSocketHandler() {}
    }

    public class MapDataServer {
        private HttpServer httpServer;
        private string publicRoot;
        private Dictionary<string, byte[]> fileCache;
        private WebSocketServiceHost webSocketHandler;
        public byte[] mapImageData;
        public Texture2D fogTexture;
        public List<ZNetPeer> players = new List<ZNetPeer>();
        public List<string> pins = new List<string>();
        public bool NeedSave = false;

        static Dictionary<string, string> contentTypes = new Dictionary<string, string>() {
            { "html", "text/html" },
            { "js", "text/javascript" },
            { "css", "text/css" },
            { "png", "image/png" }
        };

        public MapDataServer() {
            httpServer = new HttpServer(WebMapConfig.SERVER_PORT);
            httpServer.AddWebSocketService<WebSocketHandler>("/");
            httpServer.KeepClean = true;

            webSocketHandler = httpServer.WebSocketServices["/"];
          
            //Debug.Log($"WebMap: MapDataServer() interval={WebMapConfig.PLAYER_UPDATE_INTERVAL}");
            publicRoot = Path.Combine(System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "web");
            fileCache = new Dictionary<string, byte[]>();

            httpServer.OnGet += (sender, e) =>
            {
                if (ProcessSpecialAPIRoutes(e))
                {
                    return;
                }
                
                if (ProcessSpecialRoutes(e))
                {
                    return;
                }
                ServeStaticFiles(e);
            };

            httpServer.OnPost += (sender, e) =>
            {
                ProcessSpecialAPIRoutes(e);
            };
        }


        public void SendGlobalMessage(string text, int messageType=1)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", new object[]
            {
                messageType,
                "Webchat: " + text
            });
        }

        public void Stop() {
            httpServer.Stop();
        }

        private void ServeStaticFiles(HttpRequestEventArgs e) {
            var req = e.Request;
            var res = e.Response;

            var rawRequestPath = req.RawUrl;
            if (rawRequestPath == "/") {
                rawRequestPath = "/index.html";
            }

            var pathParts = rawRequestPath.Split('/');
            var requestedFile = pathParts[pathParts.Length - 1];
            var fileParts = requestedFile.Split('.');
            var fileExt = fileParts[fileParts.Length - 1];

            if (contentTypes.ContainsKey(fileExt)) {
                byte[] requestedFileBytes = new byte[0];
                if (fileCache.ContainsKey(requestedFile)) {
                    requestedFileBytes = fileCache[requestedFile];
                } else {
                    var filePath = Path.Combine(publicRoot, requestedFile);
                    try {
                        requestedFileBytes = File.ReadAllBytes(filePath);
                        if (WebMapConfig.CACHE_SERVER_FILES) {
                            fileCache.Add(requestedFile, requestedFileBytes);
                        }
                    } catch (Exception ex) {
                        Debug.Log("WebMap: FAILED TO READ FILE! " + ex.Message);
                    }
                }

                if (requestedFileBytes.Length > 0) {
                    res.Headers.Add(HttpResponseHeader.CacheControl, "public, max-age=604800, immutable");
                    res.Headers.Add("Access-Control-Allow-Origin: *");
                    res.ContentType = contentTypes[fileExt];
                    res.StatusCode = 200;
                    res.ContentLength64 = requestedFileBytes.Length;
                    res.Close(requestedFileBytes, true);
                } else {
                    res.StatusCode = 404;
                    res.Close();
                }
            } else {
                res.StatusCode = 404;
                res.Close();
            }
        }

        private bool ProcessSpecialRoutes(HttpRequestEventArgs e) {
            var req = e.Request;
            var res = e.Response;
            var rawRequestPath = req.RawUrl;
            byte[] textBytes;

            //Debug.Log($"WebMapAPI: ProcessSpecialRoutes() RawUrl: {rawRequestPath}");

            if (rawRequestPath.Contains("?"))
            {
                rawRequestPath = rawRequestPath.Substring(0, rawRequestPath.IndexOf('?'));
               // Debug.Log($"WebMapAPI: ProcessSpecialRoutes() RawUrl: {rawRequestPath}");
            }
            switch(rawRequestPath) {
                case "/config":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.Headers.Add("Access-Control-Allow-Origin: *");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    textBytes = Encoding.UTF8.GetBytes(WebMapConfig.makeClientConfigJSON());
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                case "/map":
                    // Doing things this way to make the full map harder to accidentally see.
                    res.Headers.Add(HttpResponseHeader.CacheControl, "public, max-age=604800, immutable");
                    res.Headers.Add("Access-Control-Allow-Origin: *");
                    res.ContentType = "application/octet-stream";
                    res.StatusCode = 200;
                    res.ContentLength64 = mapImageData.Length;
                    res.Close(mapImageData, true);
                    return true;
                case "/fog":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.Headers.Add("Access-Control-Allow-Origin: *");
                    res.ContentType = "image/png";
                    res.StatusCode = 200;
                    var fogBytes = fogTexture.EncodeToPNG();
                    res.ContentLength64 = fogBytes.Length;
                    res.Close(fogBytes, true);
                    return true;
                case "/pins":
                    res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                    res.Headers.Add("Access-Control-Allow-Origin: *");
                    res.ContentType = "text/csv";
                    res.StatusCode = 200;
                    var text = String.Join("\n", pins);
                    textBytes = Encoding.UTF8.GetBytes(text);
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
            }
            return false;
        }

        private bool ProcessSpecialAPIRoutes(HttpRequestEventArgs e)
        {
           
            var req = e.Request;
            var res = e.Response;
            var rawRequestPath = req.RawUrl;
            if (!rawRequestPath.StartsWith("/api/"))
            {
                return false;
            }

            if (rawRequestPath.StartsWith("/api/pins")){
                
                var sb = new StringBuilder();
                sb.AppendLine("{ \"pins\" : [");
                string delimiter = "";
                foreach (string pin in pins)
                {
                    var parts = pin.Split(new char[] { ',' }, 7);
                    sb.AppendLine(delimiter);
                    sb.AppendLine(" { ");
                    sb.AppendLine($"  \"steamid\": \"{parts[0]}\", ");
                    sb.AppendLine($"  \"pinid\": \"{parts[1]}\", ");
                    sb.AppendLine($"  \"icon\": \"{parts[2]}\", ");
                    sb.AppendLine($"  \"name\": \"{parts[3]}\", ");
                    sb.AppendLine($"  \"posx\": \"{parts[4]}\", ");
                    sb.AppendLine($"  \"posz\": \"{parts[5]}\", ");
                    sb.AppendLine($"  \"text\": \"{parts[6]}\" ");
                    sb.AppendLine(" }");
                    delimiter = ",";
                }

                sb.AppendLine("]}");

                SendHttpResponse(ref res,
                    "{ \"result\" : \"ok: " + sb.ToString() + "\" }",
                    200,
                    true,
                    "application/json");

                return true;
            }

           // Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() Will start with edit functions");
            try
            {


                if (rawRequestPath.StartsWith("/api/pin/"))
                {
                   // Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() -> /api/pin/");
                    if (e.Request.QueryString.ToString().Length == 0)
                    {
                       // Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() INVALID PAYLOAD");

                     
                        SendHttpResponse(ref res,
                            "{ \"result\" : \"invalid payload (no parms)\" }",
                            500,
                            true,
                            "application/json");

                        return true;
                    }


                    if (rawRequestPath.StartsWith("/api/pin/new/"))
                    {


                        Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() -> /api/pin/new/");
                        if (e.Request.QueryString["icon"] == null
                            ||
                            e.Request.QueryString["posx"] == null
                            ||
                            e.Request.QueryString["posz"] == null
                            ||
                            e.Request.QueryString["text"] == null
                            ||
                            e.Request.QueryString["steamid"] == null
                            ||
                            e.Request.QueryString["username"] == null
                        )
                        {
                            //Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() not enough parms");

                            SendHttpResponse(ref res,
                                "{ \"result\" : \"missing parameters\" }",
                                500,
                                true,
                                "application/json");
                            
                            return true;
                        }
                        else
                        {
                            Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() adding new pin");
                            var pos = new Vector3(float.Parse(e.Request.QueryString["posx"]), 0,
                                float.Parse(e.Request.QueryString["posz"]));
                            var safePinsText = Regex.Replace(e.Request.QueryString["text"], @"[^a-zA-Z0-9 ]", "");
                            var uuid = Guid.NewGuid().ToString();
                            
                            AddPin(e.Request.QueryString["steamid"], uuid, e.Request.QueryString["icon"],
                                e.Request.QueryString["username"], pos, safePinsText);
                          
                            SendHttpResponse(ref res,
                                "{ \"result\" : \"pin added\" }",
                                200,
                                true,
                                "application/json");

                            NeedSave = true;
                            return true;
                        }


                    }
                    else
                    {
                        //Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() will try to clean url");
                        var cleanedUrl = e.Request.RawUrl;

                        var posOfParms = e.Request.RawUrl.IndexOf("?");
                        if (posOfParms >= 0)
                        {
                            cleanedUrl =
                                e.Request.RawUrl.Substring(0, posOfParms - 1);
                        }

                        //Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() cleanedUrl: {cleanedUrl}");
                        var uuid = cleanedUrl.Substring(9);

                        //Debug.Log($" WebMapAPI: Got request for uuid {uuid}");

                        //Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() use existing uuid");
                        var idx = pins.FindIndex(u => u.Contains(uuid));
                        if (idx == -1)
                        {
                            Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() invalid uuid");
                            SendHttpResponse(ref res,
                                "{ \"result\" : \"pin not found\" }",
                                404,
                                true,
                                "application/json");


                            return true;
                        }

                        Debug.Log($" WebMapAPI: found at index {idx}: {pins[idx]}");
                        var pin = pins[idx].Split(new char[] {','}, 7);

                        //pin[6] = e.Request.QueryString.ToString();
                        if (e.Request.QueryString["removePin"] != null)
                        {
                            Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() remove pin");

                            if (!RemovePin(uuid))
                            {
                            

                                SendHttpResponse(ref res,
                                    "{ \"result\" : \"failure - could not delete\" }",
                                    500,
                                    true,
                                    "application/json");
                                return true;


                            }
                            else
                            {
                              
                                SendHttpResponse(ref res,
                                    "{ \"result\" : \"ok - deleted\" }",
                                    200,
                                    true,
                                    "application/json");
                                NeedSave = true;
                                return true;
                            }

                        }
                        else
                        {
                            //Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() edit pin");
                            if (e.Request.QueryString["icon"] != null)
                            {
                                pin[2] = e.Request.QueryString["icon"];
                            }

                            if (e.Request.QueryString["posx"] != null)
                            {
                                pin[4] = e.Request.QueryString["posx"];
                            }

                            if (e.Request.QueryString["posz"] != null)
                            {
                                pin[5] = e.Request.QueryString["posz"];
                            }

                            if (e.Request.QueryString["text"] != null)
                            {
                                pin[6] = e.Request.QueryString["text"];
                            }



                            pins[idx] = $"{pin[0]},{pin[1]},{pin[2]},{pin[3]},{pin[4]},{pin[5]},{pin[6]}";
                         
                            UpdatePin(idx);
                            SendHttpResponse(ref res,
                                "{ \"result\" : \"ok - updated to " + pins[idx] +  "\" }",
                                200,
                                true,
                                "application/json");


                            NeedSave = true;
                            return true;
                        }

                       

                    

                    }
                }
                else if (rawRequestPath.StartsWith("/api/msg/"))
                {
                    if (e.Request.QueryString["msg"] != null)
                    {
                        var messageToSend = e.Request.QueryString["msg"];
                        var typeToSend = e.Request.QueryString["type"];
                        //Debug.Log("MapDataServer.cs: Will send SendGlobalMessage(): " + messageToSend);

                        Broadcast($"webchat\n{messageToSend}");

                        SendGlobalMessage(messageToSend, 2);

                        SendHttpResponse(ref res,
                            "{ \"result\" : \"ok: " + messageToSend + "\" }",
                            200,
                            true,
                            "application/json");


                        return true;
                    }

                }

                SendHttpResponse(ref res,
                    "{ \"result\" : \"command-unknown\" }",
                    404,
                    true,
                    "application/json");

                return true;
            }
            catch (Exception ex)
            {
                SendHttpResponse(ref res,
                    "{ \"result\" : \"failure: exception on server end: "+ex+"\" }",
                    500,
                    true,
                    "application/json");
                return true;
            }

        }

        public void SendHttpResponse(ref HttpListenerResponse res, string content, int statusCode = 200 , bool noCache=true, string contentType= "application/json")
        {
            if (noCache)
            {
                res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
            }

            res.Headers.Add("Access-Control-Allow-Origin: *");
            res.ContentType = contentType;
            res.StatusCode = statusCode;
            var textBytes = Encoding.UTF8.GetBytes(content);
            res.ContentLength64 = textBytes.Length;
            res.Close(textBytes, true);

        }

        public void ListenAsync() {
            httpServer.Start();

            if (httpServer.IsListening) {
                Debug.Log($"WebMap: HTTP Server Listening on port {WebMapConfig.SERVER_PORT}");
            } else {
                Debug.Log("WebMap: HTTP Server Failed To Start !!!");
            }
        }

        public void BroadcastPing(long id, string name, Vector3 position) {
            webSocketHandler.Sessions.Broadcast($"ping\n{str(id)}\n{name}\n{str(position.x)},{str(position.z)}");
        }
        public void Broadcast(string text)
        {
            webSocketHandler.Sessions.Broadcast(text);
        }

        public void AddPin(string id, string pinId, string type, string name, Vector3 position, string pinText) {
            pins.Add($"{id},{pinId},{type},{name},{str(position.x)},{str(position.z)},{pinText}");
            webSocketHandler.Sessions.Broadcast($"pin\n{id}\n{pinId}\n{type}\n{name}\n{str(position.x)},{str(position.z)}\n{pinText}");
            NeedSave = true;
        }

        public void RemovePin(int idx) {
            var pin = pins[idx];
            var pinParts = pin.Split(',');
            pins.RemoveAt(idx);
            webSocketHandler.Sessions.Broadcast($"rmpin\n{pinParts[1]}");
        }
        public bool RemovePin(string uuid)
        {
            var idx = pins.FindIndex(u => u.Contains(uuid));
            if (idx == -1)
            {
                return false;
            }
            else
            {
                try
                {
                    var pin = pins[idx];
                    var pinParts = pin.Split(',');
                    pins.RemoveAt(idx);
                    webSocketHandler.Sessions.Broadcast($"rmpin\n{pinParts[1]}");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.Log("MapDataServer.cs: " + ex);
                    return false;
                }
               
            }

           
        }

        public void UpdatePin(int idx)
        {
            var pin = pins[idx];
            var pinParts = pin.Split(',');
            webSocketHandler.Sessions.Broadcast($"rmpin\n{pinParts[1]}");
            webSocketHandler.Sessions.Broadcast($"pin\n{pinParts[0]}\n{pinParts[1]}\n{pinParts[2]}\n{pinParts[3]}\n{pinParts[4]},{pinParts[5]}\n{pinParts[6]}");

        }
    }
}
