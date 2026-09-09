using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Policy;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BarrageGrab.Modles;
using BarrageGrab.Modles.JsonEntity;
using BarrageGrab.Proxy.ProxyEventArgs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog.LayoutRenderers;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace BarrageGrab.Proxy
{
    internal class TitaniumProxy : SystemProxy
    {
        ProxyServer proxyServer = null;
        ExplicitProxyEndPoint explicitEndPoint = null;
        ExternalProxy upStreamProxy = null;
        List<string> rinfoRequestings = new List<string>();

        const string SCRIPT_HOST = "lf-cdn-tos.bytescm.com";
        const string LIVE_HOST = "live.douyin.com";
        const string DOUYIN_HOST = "www.douyin.com";
        const string USER_INFO_PATH = "/webcast/user/me/";
        const string BARRAGE_POOL_PATH = "/webcast/im/fetch";
        const string LIVE_SCRIPT_PATH = "/obj/static/webcast/douyin_live";
        const string WEBCAST_AMEMV_HOST = "webcast.amemv.com";
        private readonly Regex webcastBarrageReg = new Regex(@"webcast\d+-ws-web-\w+\.(douyin|amemv)\.com");

        public override string HttpUpstreamProxy { get { return proxyServer?.UpStreamHttpProxy?.ToString() ?? ""; } }

        public override string HttpsUpstreamProxy { get { return proxyServer?.UpStreamHttpsProxy?.ToString() ?? ""; } }

        static TitaniumProxy()
        {
            // 设置代理过滤规则
            string[] bypassList = { "localhost", "127.*", "10.*", "172.16.*", "172.17.*", "172.18.*", "172.19.*",
                                "172.20.*", "172.21.*", "172.22.*", "172.23.*", "172.24.*", "172.25.*",
                                "172.26.*", "172.27.*", "172.28.*", "172.29.*", "172.30.*", "172.31.*",
                                "192.168.*" };

            // 创建WebProxy对象，并设置代理过滤规则
            WebProxy proxy = new WebProxy
            {
                BypassList = bypassList,
                UseDefaultCredentials = true
            };

            // 设置不使用回环地址
            proxy.BypassProxyOnLocal = true;

            try
            {
                // 将代理设置为系统默认代理
                WebRequest.DefaultWebProxy = proxy;
            }
            catch (Exception ex)
            {
                Logger.LogWarn("代理环境设置失败：" + ex.Message);
            }
        }

        public TitaniumProxy()
        {
            //注册系统代理
            //RegisterSystemProxy();
            proxyServer = new ProxyServer();

            proxyServer.ReuseSocket = false;
            proxyServer.EnableConnectionPool = true;
            proxyServer.ForwardToUpstreamGateway = true;

            proxyServer.CertificateManager.CertificateValidDays = 365 * 10;
            proxyServer.CertificateManager.SaveFakeCertificates = true;
            proxyServer.CertificateManager.CertificateEngine = Titanium.Web.Proxy.Network.CertificateEngine.DefaultWindows;
            proxyServer.CertificateManager.OverwritePfxFile = false;
            proxyServer.CertificateManager.RootCertificate = GetCert();
            if (proxyServer.CertificateManager.RootCertificate == null)
            {
                Logger.PrintColor("正在进行证书安装，需要信任该证书才可进行https解密，若有提示请确定");
                proxyServer.CertificateManager.CreateRootCertificate();
            }
            proxyServer.CertificateManager.TrustRootCertificate(true);

            //https://github.com/justcoding121/titanium-web-proxy/issues/828

            proxyServer.ServerCertificateValidationCallback += ProxyServer_ServerCertificateValidationCallback;
            //proxyServer.BeforeRequest += ProxyServer_BeforeRequest;
            proxyServer.BeforeResponse += ProxyServer_BeforeResponse;
            //proxyServer.AfterResponse += ProxyServer_AfterResponse;            

            explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, ProxyPort, true);
            explicitEndPoint.BeforeTunnelConnectRequest += ExplicitEndPoint_BeforeTunnelConnectRequest;
            proxyServer.AddEndPoint(explicitEndPoint);
        }

        private X509Certificate2 GetCert()
        {
            X509Certificate2 result = proxyServer.CertificateManager.LoadRootCertificate();

            if (result != null) return result;

            // 打开“受信任的根证书颁发机构”存储区
            using (X509Store store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);

                // 遍历证书集合
                foreach (X509Certificate2 cert in store.Certificates)
                {
                    try
                    {
                        // 尝试使用证书创建 X509Certificate2 实例
                        X509Certificate2 certificate = new X509Certificate2(cert);

                        //判断过期
                        if (DateTime.Now > certificate.NotAfter) continue;

                        //判断黑名单
                        var black = new string[] { "localhost" };
                        if (certificate.FriendlyName.ToLower().LikeIn(black) ||
                            certificate.Subject.ToLower().LikeIn(black)
                           ) continue;


                        if (certificate.FriendlyName.ToLower().LikeIn("titanium"))
                        {
                            result = certificate;
                            break;
                        }

                        // 打印证书信息
                        //Console.WriteLine("证书: " + certificate.Subject);
                        //Console.WriteLine("颁发者: " + certificate.Issuer);
                        //Console.WriteLine("有效期: " + certificate.NotBefore + " - " + certificate.NotAfter);

                        //result = certificate;
                        //break;
                    }
                    catch (CryptographicException ex)
                    {
                        //捕获加密异常，如果证书需要密码，这里会抛出异常
                        throw new Exception("证书加载失败: " + ex.Message);
                    }
                }

                store.Close();
            }

            return result;
        }

        public override void SetUpstreamProxy(string upstreamProxyAddr)
        {
            if (string.IsNullOrWhiteSpace(upstreamProxyAddr)) return;
            upstreamProxyAddr = upstreamProxyAddr.Trim();
            var reg = new Regex(@"[a-zA-Z0-9\.]+:\d+");
            if (!reg.IsMatch(upstreamProxyAddr))
            {
                throw new Exception("上游代理地址格式不正确，必须为ip:port格式");
            }
            //设置上游代理地址
            //var upstreamProxyAddr = Appsetting.Current.UpstreamProxy;
            if (!upstreamProxyAddr.IsNullOrWhiteSpace())
            {
                upStreamProxy = new ExternalProxy()
                {
                    HostName = upstreamProxyAddr.Split(':')[0],
                    Port = int.Parse(upstreamProxyAddr.Split(':')[1]),
                    ProxyType = ExternalProxyType.Http
                };
                proxyServer.UpStreamHttpProxy = upStreamProxy;
                proxyServer.UpStreamHttpsProxy = upStreamProxy;
            }
        }

        private bool CheckBrowser(string processName)
        {
            return AppSetting.Current.ProcessFilter.Contains(processName) && processName != "直播伴侣" && processName != "douyin";
        }

        private async Task ProxyServer_BeforeResponse(object sender, SessionEventArgs e)
        {
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var contentType = e.HttpClient.Response.ContentType ?? "";

            //处理直播伴侣开播更新
            await HookLiveCompanStartLive(e);

            //处理直播伴侣下播
            await HookLiveCompanEndLive(e);

            //处理弹幕
            await HookBarrage(e);

            //处理JS注入
            await HookPageAsync(e);

            //处理脚本拦截修改
            await HookScriptAsync(e);
        }

        // Hook 直播伴侣开播信息并更新
        private async Task HookLiveCompanStartLive(SessionEventArgs e)
        {
            //Hook 直播伴侣直播间创建 https://webcast.amemv.com/webcast/room/create
            string uri = e.HttpClient.Request.RequestUri.ToString();
            var urix = new Uri(uri);
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var response = e.HttpClient.Response;            
            if (!urix.AbsolutePath.TrimEnd('/').EndsWith("/webcast/room/create")) return;
            if (processName != "直播伴侣") return;
            if (response.StatusCode != 200) return;
            var reponse = await e.GetResponseBodyAsString();
            RoomInfo roomInfo;
            //缓存直播间信息
            var tupe = RoomInfo.TryParseStreamPusherCreate(reponse, out roomInfo);
            var code = tupe.Item1;
            var msg = tupe.Item2;

            if (code != 0)
            {
                Logger.LogWarn($"直播伴侣开播房间资料缓存失败，原因:{msg}");
                return;
            }

            var jobj = JsonConvert.DeserializeObject<JObject>(reponse);

            var roomid = jobj["data"]?["id_str"]?.Value<string>();
            var sec_uid = jobj["data"]?["owner"]?["sec_uid"]?.Value<string>();
            var nickname = jobj["data"]?["owner"]?["nickname"]?.Value<string>();
            var displayId = jobj["data"]?["owner"]?["display_id"]?.Value<string>();

            if (roomInfo != null && !roomid.IsNullOrWhiteSpace())
            {
                roomInfo.RoomId = roomid;
                roomInfo.Title = jobj["data"]?["title"]?.Value<string>();
                Logger.LogInfo($"直播伴侣开播，开播账号:{displayId} {nickname} ，更新RoomId={roomInfo.RoomId}");
            }

            if (roomInfo != null)
            {
                AppRuntime.RoomCaches.AddRoomInfoCache(roomInfo);
            }

            FireLiveCompanEvent(new LiveCompanEventArgs()
            {
                Action = 1,
                Data = roomInfo,
                ProcessId = processid,
                ProcessName = processName
            });
        }

        // Hook 直播伴侣关播
        private async Task HookLiveCompanEndLive(SessionEventArgs e)
        {
            //Hook 直播伴侣关闭直播 https://webcast5-mate-lf.amemv.com/webcast/room/anchor_finish_info
            string uri = e.HttpClient.Request.RequestUri.ToString();
            var urix = new Uri(uri);
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var response = e.HttpClient.Response;
            if (!urix.AbsolutePath.TrimEnd('/').EndsWith("/webcast/room/anchor_finish_info")) return;            
            if (processName != "直播伴侣") return;
            if (response.StatusCode != 200) return;
            var reponse = await e.GetResponseBodyAsString();

            dynamic jobj = JsonConvert.DeserializeObject<JObject>(reponse);
            JObject owner = jobj?.data?.owner?.ToObject<JObject>();

            if (owner == null) return;

            var info = new AnchorFinishInfo()
            {
                Nickname = owner["nickname"].Value<string>(),
                HeadUrl = owner["avatar_thumb"]?["url_list"]?.Values<string>().FirstOrDefault() ?? "",
                SecUid = owner["sec_uid"]?.Value<string>() ?? "",
                UserId = owner["id"]?.Value<long>() ?? 0,
                DisplayId = owner["display_id"]?.Value<string>() ?? "",
                RoomId = owner["own_room"]?["room_ids_str"]?.Values<string>().FirstOrDefault() ?? ""
            };

            if (info.UserId == 0) return;

            Logger.LogInfo($"直播伴侣关播，关播账号:{info.DisplayId} {info.Nickname} RoomId={info.RoomId}");

            FireLiveCompanEvent(new LiveCompanEventArgs()
            {
                Action = 2,
                Data = info,
                ProcessId = processid,
                ProcessName = processName
            });
        }

        // Hook 弹幕
        private async Task HookBarrage(SessionEventArgs e)
        {
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var contentType = e.HttpClient.Response.ContentType ?? "";
            var isLiveCompan = processName == "直播伴侣";

            //ws 方式
            if (
                e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket &&
                webcastBarrageReg.IsMatch(uri)
               )
            {
                e.DataReceived += WebSocket_DataReceived;
                e.DataSent += WebSocket_DataSent;
                var urix = new Uri(uri);
                var roomid = urix.GetQueryParam("room_id");
                Logger.LogInfo($"订阅到新的弹幕流地址，roomid:{roomid}");
            }

            //轮询方式(当抖音ws连接断开后，客户端也会降级使用轮询模式获取弹幕)
            if (uri.Contains(BARRAGE_POOL_PATH) && contentType.Contains("application/protobuffer"))
            {
                var payload = await e.GetResponseBody();

                var referrer = e.HttpClient.Request.Headers.GetFirstHeader("Referer")?.Value;
                //https://live.douyin.com/22404217360

                //检查webroomid映射
                if (!referrer.IsNullOrWhiteSpace())
                {
                    var webroomid = Regex.Match(referrer, @"(?<=live\.douyin\.com\/)\d+").Value;
                    var urix = new Uri(uri);
                    var roomid = urix.GetQueryParam("room_id");
                    var cookie = e.HttpClient.Request.Headers.GetFirstHeader("Cookie")?.Value;

                    var roomInfo = AppRuntime.RoomCaches.GetCachedWebRoomInfo(roomid);
                    if (roomInfo == null && cookie != null && !rinfoRequestings.Contains(webroomid))
                    {
                        lock (rinfoRequestings) rinfoRequestings.Add(webroomid);
                        //查询后会自动缓存
                        _ = DyApiHelper.GetRoomInfoForApi(webroomid, cookie).ContinueWith(t =>
                        {
                            var rinfo = t.Result;
                            //限制只尝试一次
                            if (rinfo != null)
                            {
                                lock (rinfoRequestings) rinfoRequestings.Remove(webroomid);
                            }
                        });
                    }

                    AppRuntime.RoomCaches.SetRoomCache(roomid, webroomid);
                }

                base.FireOnFetchResponse(new HttpResponseEventArgs()
                {
                    HttpClient = e.HttpClient,
                    ProcessID = processid,
                    HostName = hostname,
                    ProcessName = base.GetProcessName(processid),
                    Payload = payload
                });
            }
        }

 

        // Hook 直播页面
        private async Task HookPageAsync(SessionEventArgs e)
        {
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            string url = e.HttpClient.Request.Url;
            var urlNoQuery = url.Split('?')[0];
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var liveRoomMactch = Regex.Match(uri.Trim(), @".*:\/\/live.douyin\.com\/([0-9a-zA-Z_]+)");
            var liveHomeMatch = Regex.Match(uri.Trim(), @".*:\/\/live.douyin\.com\/?$");
            var contentType = e.HttpClient.Response.ContentType ?? "";

            //检测是否为dom页，用于脚本注入
            if (!contentType.Contains("text/html")) return;

            //如果响应头含有 CSP(https://blog.csdn.net/qq_30436011/article/details/127485927 会阻止内嵌脚本执行) 则删除
            var csp = e.HttpClient.Response.Headers.GetFirstHeader("Content-Security-Policy");
            if (csp != null)
            {
                e.HttpClient.Response.Headers.RemoveHeader("Content-Security-Policy");
            }

            //获取 content-type                
            if (liveRoomMactch.Success)
            {
                string webrid = liveRoomMactch.Groups[1].Value;
                var html = await e.GetResponseBodyAsString();

                //利用 HtmlAgilityPack 在尾部注入script 标签
                RoomInfo roominfo;
                var tup = RoomInfo.TryParseRoomPageHtml(html, out roominfo);
                int code = tup.Item1;
                string msg = tup.Item2;

                if (code == 0)
                {
                    Logger.LogInfo($"直播页{webrid} [{roominfo.Owner.Nickname}]的直播间，房间信息已采集到缓存");
                    roominfo.WebRoomId = webrid;
                    roominfo.LiveUrl = url;
                }
                else
                {
                    roominfo = new RoomInfo();
                    roominfo.WebRoomId = webrid;
                    roominfo.LiveUrl = url;
                    //正则匹配主播标题
                    //<div class="st8eGKi4" data-e2e="live-room-nickname">和平精英小夜y</div>
                    var match = Regex.Match(html, @"(?<=live-room-nickname""\>).+(?=<\/div>)");
                    if (match.Success)
                    {
                        roominfo.Owner = new RoomInfo.RoomAnchor()
                        {
                            Nickname = match.Value,
                            UserId = "-1"
                        };
                    }                    
                }
                AppRuntime.RoomCaches.AddRoomInfoCache(roominfo);

                //控制自动暂停
                if (AppSetting.Current.AutoPause)
                {
                    html = html.Replace("autoplay&quot;:true", "autoplay&quot;:false");
                    Logger.LogInfo("已禁用直播自动播放");
                }

                e.SetResponseBodyString(html);
            }            
        }
     
        //生成注入上下文
        private string BuildContext(IDictionary<string, string> constVals)
        {
            var scriptContext = string.Join("\r\n", constVals.Select(s =>
            {
                return "const " + s.Key + " = " + s.Value + ";";
            }));
            return scriptContext;
        }

        // Hook Script 脚本
        private async Task HookScriptAsync(SessionEventArgs e)
        {
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            string url = e.HttpClient.Request.Url;
            var urlNoQuery = url.Split('?')[0];
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var contentType = e.HttpClient.Response.ContentType ?? "";
            var fileName = Path.GetFileName(urlNoQuery);

            if (contentType == null) return;
            if (!contentType.Trim().ToLower().Contains("application/javascript")) return;
            if (e.HttpClient.Response.StatusCode != 200) return;

            //https://lf-webcast-platform.bytetos.com/obj/webcast-platform-cdn/webcast/douyin_live/chunks/island_a74ce.b55095a0.js
            //https://lf-webcast-platform.bytetos.com/obj/webcast-platform-cdn/webcast/douyin_live/chunks/PausePop.ec38fcf8.js 2026/04/09
            //判断响应内容是否为js application/javascript
            if (processName != "直播伴侣" && processName != "douyin"
                && fileName.ToLower().StartsWith("pausepop")
                )
            {
                var js = await e.GetResponseBodyAsString();
                var reg = new Regex(@"if\(!\(\d{1,},\w{1,}\.DJ\)\(\).+\w{1,}\.includes\(""live""\)\)\)\{");
                var match = reg.Match(js);
                if (match.Success)
                {
                    js = reg.Replace(js, "if(false){");
                    e.SetResponseBodyString(js);
                    Logger.PrintColor($"已成功绕过JS页面无操作检测 {urlNoQuery}\n", ConsoleColor.Green);
                }
            }

            if (url.Contains(LIVE_SCRIPT_PATH) && AppSetting.Current.ForcePolling)
            {
                var reg = new Regex(@"if\s*\((?<patt>!this\.stopPolling)\)");
                var js = await e.GetResponseBodyAsString();
                var match = reg.Match(js);
                if (match.Success)
                {
                    var pollingIntervalReg = new Regex(@"this\.errorInterval\s*=(?<value>.+?),");
                    var pollingIntervalMatch = pollingIntervalReg.Match(js);
                    if (pollingIntervalMatch.Success)
                    {
                        var myValue = AppSetting.Current.PollingInterval;
                        js = pollingIntervalReg.Replace(js, $"this.pollingInterval={myValue},");
                        Logger.PrintColor($"直播间已成功修改轮询间隔为{myValue}ms", ConsoleColor.Green);
                    }

                    js = reg.Replace(js, "if(true)");
                    e.SetResponseBodyString(js);
                    Logger.PrintColor($"直播间已强制启用Http轮询模式", ConsoleColor.Green);
                }
            }
        }

        private Task ProxyServer_ServerCertificateValidationCallback(object sender, CertificateValidationEventArgs e)
        {
            // set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None)
            {
                e.IsValid = true;
            }
            return Task.CompletedTask;
        }

        //控制要解密SSL的域名
        private Task ExplicitEndPoint_BeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            string url = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;

            e.DecryptSsl = CheckHost(hostname);


            //ws 方式
            //if (
            //    e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket &&
            //    webcastBarrageReg.IsMatch(url)
            //   )
            //{
            //    e.HttpClient.Request.RequestUri = new Uri("wss:////localhost:8828");
            //}
            return Task.CompletedTask;
        }

        //检测域名白名单
        protected override bool CheckHost(string hostname)
        {
            //需要解析SSL的域名 放在这里，全开会导致性能问题，应只解析业务需要的域名
            var decryptSsls = new string[]
            {
                SCRIPT_HOST,
                LIVE_HOST,
                WEBCAST_AMEMV_HOST , //直播伴侣开播请求地址
                "*-webcast-platform.bytetos.com", //新的脚本地址
                "*webcast*" //所有带webcast的域名
            };

            if (decryptSsls.Contains(hostname))
            {
                return true;
            }

            if (hostname.WildcardMatchAny(decryptSsls))
            {
                return true;
            }

            hostname = hostname.Trim().ToLower();
            return base.CheckHost(hostname);
        }

        private void WebSocket_DataSent(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;

            string hostname = args.HttpClient.Request.RequestUri.Host;

            var processid = args.HttpClient.ProcessId.Value;

            List<byte> messageData = new List<byte>();

            foreach (var frame in args.WebSocketDecoderReceive.Decode(e.Buffer, e.Offset, e.Count))
            {
                if (frame.OpCode == WebsocketOpCode.Continuation)
                {
                    messageData.AddRange(frame.Data.ToArray());
                    continue;
                }
                else
                {
                    //读取完毕
                    byte[] payload;
                    if (messageData.Count > 0)
                    {
                        messageData.AddRange(frame.Data.ToArray());
                        payload = messageData.ToArray();
                        messageData.Clear();
                    }
                    else
                    {
                        payload = frame.Data.ToArray();
                    }

                    var str = Encoding.UTF8.GetString(payload);

                    //base.FireWsEvent(new WsMessageEventArgs()
                    //{
                    //    ProcessID = processid,
                    //    HostName = hostname,
                    //    Payload = payload,
                    //    ProcessName = base.GetProcessName(processid)
                    //});
                }
            }
        }

        //WebSocket 流读取
        private void WebSocket_DataReceived(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;

            string hostname = args.HttpClient.Request.RequestUri.Host;

            var processid = args.HttpClient.ProcessId.Value;

            List<byte> messageData = new List<byte>();

            try
            {
                foreach (var frame in args.WebSocketDecoderReceive.Decode(e.Buffer, e.Offset, e.Count))
                {
                    if (frame.OpCode == WebsocketOpCode.Continuation)
                    {
                        messageData.AddRange(frame.Data.ToArray());
                        continue;
                    }
                    else
                    {
                        //读取完毕
                        byte[] payload;
                        if (messageData.Count > 0)
                        {
                            messageData.AddRange(frame.Data.ToArray());
                            payload = messageData.ToArray();
                            messageData.Clear();
                        }
                        else
                        {
                            payload = frame.Data.ToArray();
                        }

                        base.FireWsEvent(new WsMessageEventArgs()
                        {
                            ProcessID = processid,
                            HostName = hostname,
                            Payload = payload,
                            ProcessName = base.GetProcessName(processid)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.PrintColor("解析某个WebSocket包出错：" + ex.Message);
            }

            if (messageData.Count > 0)
            {
                // 没有收到 WebSocket 帧的结束帧，抛出异常或者进行处理
            }

        }


        /// <summary>
        /// 释放资源，关闭系统代理
        /// </summary>
        override public void Dispose()
        {
            proxyServer.Stop();
            proxyServer.Dispose();
            if (AppSetting.Current.UsedProxy)
            {
                CloseSystemProxy();
            }
        }

        /// <summary>
        /// 启动监听
        /// </summary>
        override public void Start()
        {
            proxyServer.Start(false);

            if (AppSetting.Current.UsedProxy)
            {
                base.RegisterSystemProxy();
                Logger.LogInfo($"系统代理代理已启动，127.0.0.1:{base.ProxyPort}");
                //使用其自带的系统代理设置可能会导致格式问题
                //proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
                //proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
            }
            else
            {
                Logger.LogInfo($"代理已启动(局域代理)，127.0.0.1:{base.ProxyPort}");
            }
        }
    }
}
