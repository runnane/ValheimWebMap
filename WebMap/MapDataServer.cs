using System;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Reflection;
using HarmonyLib;
using WebSocketSharp.Net;
using WebSocketSharp.Server;

using UnityEngine;
using static WebMap.WebMapConfig;
using System.Text.RegularExpressions;

namespace WebMap {

    public class WebSocketHandler : WebSocketBehavior {
        public WebSocketHandler() {}
        // protected override void OnOpen() {
        //     Context.WebSocket.Send("hi " + ID);
        // }

        // protected override void OnClose(CloseEventArgs e) {
        // }

        // protected override void OnMessage(MessageEventArgs e) {
        //     Sessions.Broadcast(e.Data);
        // }
    }

    public class MapDataServer {
        private HttpServer httpServer;
        private string publicRoot;
        private Dictionary<string, byte[]> fileCache;
        private System.Threading.Timer broadcastTimer;
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
          
            Debug.Log($"WebMap: MapDataServer() interval={WebMapConfig.PLAYER_UPDATE_INTERVAL}");
            broadcastTimer = new System.Threading.Timer((e) => {
            var dataString = "";
            players.ForEach(player =>
            {
                ZDO zdoData = null;
                try
                {
                    zdoData = ZDOMan.instance.GetZDO(player.m_characterID);
                }
                catch
                {
                }

                if (zdoData != null)
                {
                    var pos = zdoData.GetPosition();
                    var maxHealth = zdoData.GetFloat("max_health", 25f);
                    var health = zdoData.GetFloat("health", maxHealth);
                    maxHealth = Mathf.Max(maxHealth, health);

                    if (player.m_publicRefPos)
                    {
                        dataString +=
                            $"{player.m_uid}\n{player.m_playerName}\n{str(pos.x)},{str(pos.y)},{str(pos.z)}\n{str(health)}\n{str(maxHealth)}\n\n";
                    }
                    else
                    {
                        dataString += $"{player.m_uid}\n{player.m_playerName}\nhidden\n\n";
                    }
                    //Debug.Log("WebMap: Broadcasting");
                }
                else
                {
                    // Debug.Log("WebMap: Will not broadcast") ;
                }
            });
                if (dataString.Length > 0) {
                    webSocketHandler.Sessions.Broadcast("players\n" + dataString.Trim());
                }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(WebMapConfig.PLAYER_UPDATE_INTERVAL));

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

        public void Stop() {
            broadcastTimer.Dispose();
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
           // Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() Url: {e.Request.Url}"); 
            Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() RawUrl: {e.Request.RawUrl}");
            Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() QueryString: {e.Request.QueryString}");
            

            var req = e.Request;
            var res = e.Response;
            var rawRequestPath = req.RawUrl;
            byte[] textBytes;
            if (!rawRequestPath.StartsWith("/api/"))
            {
                return false;
            }

            if (rawRequestPath.StartsWith("/api/pins")){
                res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                res.Headers.Add("Access-Control-Allow-Origin: *");
                res.ContentType = "application/json";
                res.StatusCode = 200;

                /*
                 steamid, id, icon, name, x, y, text
                76561197968706811,1620727876497.06 - 2741,dot,Modder,68.42,-4.60,123
                76561197968706811,1620727887643.06 - 8869,dot,Modder,64.66,-4.31,321
                */
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
                textBytes = Encoding.UTF8.GetBytes(sb.ToString());
                res.ContentLength64 = textBytes.Length;
                res.Close(textBytes, true);
                return true;
            }

            Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() Will start with edit functions");

            if (rawRequestPath.StartsWith("/api/pin/"))
            {
                Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() -> /api/pin/");
                if (e.Request.QueryString.ToString().Length == 0)
                {
                    Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() INVALID PAYLOAD");
                    res.StatusCode = 500;
                    textBytes = Encoding.UTF8.GetBytes("{ \"result\" : \"invalid payload (no parms)\" }");
                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;
                }
                


                res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
                res.Headers.Add("Access-Control-Allow-Origin: *");
                res.ContentType = "application/json";

              
              
                if(rawRequestPath.StartsWith("/api/pin/new/")){

                
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
                        Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() not enough parms"); 
                        res.StatusCode = 500;
                        textBytes = Encoding.UTF8.GetBytes("{ \"result\" : \"missing parameters\" }");
                        res.ContentLength64 = textBytes.Length;
                        res.Close(textBytes, true);
                        return true;
                    }
                    else
                    {
                        Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() adding new pin");
                        var pos = new Vector3(float.Parse(e.Request.QueryString["posx"]), 0, float.Parse(e.Request.QueryString["posz"]));
                        var safePinsText = Regex.Replace(e.Request.QueryString["text"], @"[^a-zA-Z0-9 ]", "");
                        var uuid = Guid.NewGuid().ToString();
                        //var timestamp = DateTime.Now - unixEpoch;
                        // var pinId = $"{timestamp.TotalMilliseconds}-{UnityEngine.Random.Range(1000, 9999)}";
                        // var pinId = uuId.ToString();
                        AddPin(e.Request.QueryString["steamid"], uuid, e.Request.QueryString["icon"], e.Request.QueryString["username"], pos, safePinsText);
                        res.StatusCode = 200;
                        textBytes = Encoding.UTF8.GetBytes("{ \"result\" : \"pin added\" }");
                        res.ContentLength64 = textBytes.Length;
                        res.Close(textBytes, true);
                        return true;
                    }
                   
                   
                }
                else
                {
                    Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() will try to clean url");
                    var cleanedUrl = e.Request.RawUrl;

                    var posOfParms = e.Request.RawUrl.IndexOf("?");
                    if (posOfParms >= 0)
                    {
                        cleanedUrl =
                            e.Request.RawUrl.Substring(0, posOfParms - 1);
                    }
                    Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() cleanedUrl: {cleanedUrl}");
                    var uuid = cleanedUrl.Substring(9);

                    Debug.Log($" WebMapAPI: Got request for uuid {uuid}");

                    Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() use existing uuid");
                    var idx = pins.FindIndex(u => u.Contains(uuid));
                    if (idx == -1)
                    {
                        Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() invalid uuid");
                        res.StatusCode = 404;
                        textBytes = Encoding.UTF8.GetBytes("{ \"result\" : \"pin not found\" }");
                        res.ContentLength64 = textBytes.Length;
                        res.Close(textBytes, true);
                        return true;
                    }

                    Debug.Log($" WebMapAPI: found at index {idx}: {pins[idx]}");
                    var pin = pins[idx].Split(new char[] {','}, 7);

                    //pin[6] = e.Request.QueryString.ToString();
                    if (e.Request.QueryString["removePin"] != null)
                    {
                        Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() remove pin");
                        res.StatusCode = 200;
                        RemovePin(idx);
                        textBytes = Encoding.UTF8.GetBytes("{ \"result\" : \"ok - deleted\" }");
                    }
                    else
                    {
                        Debug.Log($"WebMapAPI: ProcessSpecialAPIRoutes() edit pin");
                        if (e.Request.QueryString["icon"] != null)
                        {
                            pin[3] = e.Request.QueryString["icon"];
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
                        res.StatusCode = 200;
                        textBytes = Encoding.UTF8.GetBytes("{ \"result\" : \"ok - updated\" }");
                        UpdatePin(idx);
                    }

                    NeedSave = true;

                    res.ContentLength64 = textBytes.Length;
                    res.Close(textBytes, true);
                    return true;

                }
            }

            res.Headers.Add(HttpResponseHeader.CacheControl, "no-cache");
            res.ContentType = "application/json";
            res.StatusCode = 404;
            textBytes = Encoding.UTF8.GetBytes("{ \"result\" : \"command-unknown\" }");
            res.ContentLength64 = textBytes.Length;
            res.Close(textBytes, true);
            return true;

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

        public void UpdatePin(int idx)
        {
            var pin = pins[idx];
            var pinParts = pin.Split(',');
            webSocketHandler.Sessions.Broadcast($"rmpin\n{pinParts[1]}");
            webSocketHandler.Sessions.Broadcast($"pin\n{pinParts[0]}\n{pinParts[1]}\n{pinParts[2]}\n{pinParts[3]}\n{pinParts[4]},{pinParts[5]}\n{pinParts[6]}");

        }
    }
}
