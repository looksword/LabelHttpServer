using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Configuration;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using Seagull.BarTender.Print;
using Newtonsoft.Json;


namespace LabelHttpServer
{
    public partial class LabelHttpServer : IDisposable
    {
        #region initial

        private TcpTransport transHB = null;
        private System.Timers.Timer hbDetecter = new System.Timers.Timer();
        public event HandleStringMsg OnMsg;

        private RollingFileWriterManager rfwManager = null;
        private bool enableWriteFile = false;
        private int maxFileKiloByte = 10000;
        private int maxFileNum = 10;

        public string currDir = "";
        public string TSCDirectory = "";

        static HttpListener httpobj;
        private string httpip = "127.0.0.1";
        private int httpport = 9037;

        public string printer = "TSC TTP-244 Pro";
        public TSCSDK.usb usbprint = new TSCSDK.usb();
        //public TSCSDK.driver driver = new TSCSDK.driver();

        private System.Timers.Timer PrintDetecter = new System.Timers.Timer();

        private int refreshListen = 0;

        #endregion

        public LabelHttpServer()
        {
            try
            {
                enableWriteFile = bool.Parse(ConfigurationManager.AppSettings["EnableWriteFile"]);
            }
            catch (Exception) { }
            if (enableWriteFile)
            {
                try
                {
                    maxFileKiloByte = int.Parse(ConfigurationManager.AppSettings["MaxFileKiloByte"]);
                }
                catch (Exception) { }

                try
                {
                    maxFileNum = int.Parse(ConfigurationManager.AppSettings["MaxFileNum"]);
                }
                catch (Exception) { }

                rfwManager = new RollingFileWriterManager(maxFileKiloByte, maxFileNum);

                try
                {
                    rfwManager.WriteInterval = int.Parse(ConfigurationManager.AppSettings["WriteFileInterval"]);
                }
                catch (Exception) { }

                try
                {
                    rfwManager.FileNameSuffix = ConfigurationManager.AppSettings["FileNameSuffix"];
                }
                catch (Exception) { }

                try
                {
                    rfwManager.OutputPath = ConfigurationManager.AppSettings["OutputPath"];
                }
                catch (Exception) { }

                rfwManager.OnErrMsg += rfwManagerOnErrMsg;
                rfwManager.StartWrite();
            }

            try
            {
                httpip = ConfigurationManager.AppSettings["HttpIP"];
                httpport = int.Parse(ConfigurationManager.AppSettings["HttpPort"]);
            }
            catch (Exception) { }

            try
            {
                printer = ConfigurationManager.AppSettings["TSCPort"];
            }
            catch (Exception) { }

            currDir = Assembly.GetExecutingAssembly().Location.ToString();
            string[] dirs = currDir.Split('\\', '/');
            currDir = "";
            for (int i = 0; i < dirs.Length - 1; i++)
            {
                if (i > 0)
                {
                    currDir += "\\";
                }
                currDir += dirs[i];
            }
            DirectoryInfo dirInfo = new DirectoryInfo(currDir + "\\Data\\BarTender\\");
            if (!dirInfo.Exists)
                dirInfo.Create();
            TSCDirectory = currDir + "\\Data\\BarTender\\";

            //查找设备


            //定时搜索打印机
            PrintDetecter.Elapsed += PrintDetecter_Elapsed;
            PrintDetecter.Interval = 30000;
        }

        #region base

        private DateTime lastHeartbeatTime = DateTime.Now;// 上次心跳时间
        private void transHBOnReceivedData(byte[] data, int dataLen)
        {
            lastHeartbeatTime = DateTime.Now;// 收到心跳信息 更新上次心跳时间
        }
        private void hbDetecterElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            TimeSpan ts = DateTime.Now - lastHeartbeatTime;
            if (ts.TotalSeconds > 12)
            {
                hbDetecter.Stop();
                hbDetecter.Elapsed -= hbDetecterElapsed;
                if (transHB != null)
                {
                    transHB.Stop();
                    transHB.OnReceivedData -= transHBOnReceivedData;
                }
                Stop();
                Environment.Exit(-1);
            }
            if (transHB != null)
            {
                transHB.AsyncSendData(new byte[] { 0x0A, 0x03 });
            }
        }
        public void Init(ushort heartbeatPort)
        {
            if (heartbeatPort > 0)
            {
                transHB = new TcpTransport
                {
                    IP = "127.0.0.1",
                    Port = heartbeatPort
                };
                transHB.OnReceivedData += transHBOnReceivedData;
                transHB.Start();

                lastHeartbeatTime = DateTime.Now;
                hbDetecter.Elapsed += hbDetecterElapsed;
                hbDetecter.Interval = 1000;
                hbDetecter.Start();
            }
        }
        public void Start()
        {
            try
            {
                //提供一个简单的、可通过编程方式控制的 HTTP 协议侦听器。此类不能被继承。
                httpobj = new HttpListener();
                //定义url及端口号，通常设置为配置文件
                string httpaddr = "http://" + httpip + ":" + httpport.ToString() + "/";
                //string httpaddr = $"http://{httpip}:{httpport}/";
                httpobj.Prefixes.Add(httpaddr);

                httpobj.TimeoutManager.IdleConnection = TimeSpan.FromSeconds(10);

                //启动监听器
                httpobj.Start();
                //异步监听客户端请求，当客户端的网络请求到来时会自动执行Result委托
                //该委托没有返回值，有一个IAsyncResult接口的参数，可通过该参数获取context对象
                httpobj.BeginGetContext(Result, null);
                ShowMsg(StringMsgType.Info, "ALL", "Start listen to : " + httpaddr);

                PrintDetecter.Start();
            }
            catch (Exception ex)
            {
                ShowMsg(StringMsgType.Error, "ALL", "Failed to do auto start. " + ex.Message);
                Environment.Exit(-1);
            }
        }
        public void Stop()
        {
            ShowMsg(StringMsgType.Info, "Base", "即将关闭后台进程");
            try
            {
                httpobj.Stop();
                httpobj.Abort();
                httpobj.Close();
            }
            catch
            { }

            PrintDetecter.Stop();

            Thread.Sleep(2000);
            if (rfwManager != null)
            {
                rfwManager.StopWrite();
                Thread.Sleep(rfwManager.WriteInterval + 1000);
            }
        }
        public void Dispose()
        {
            try
            {
                Stop();
            }
            catch { }
        }
        public void ShowMsg(StringMsgType msgType, string machineID, string msg)
        {
            if (msgType == StringMsgType.Info || msgType == StringMsgType.Warning || msgType == StringMsgType.Error)
            {
                try
                {
                    if (OnMsg != null)
                    {
                        try { OnMsg(msgType, machineID + ": " + msg + "\r\n"); }
                        catch { }
                    }
                }
                catch
                {

                }
            }
            if (rfwManager != null)
            {
                if (msgType == StringMsgType.Data)
                {
                    return;
                }
                rfwManager.AddData(machineID, msgType.ToString(), DateTime.Now.ToString("yyyyMMdd HH:mm:ss.fff") + ": " + msg + "\r\n");
            }
        }
        private void rfwManagerOnErrMsg(string errMsg)
        {
            ShowMsg(StringMsgType.Error, "FileWriter", errMsg);
        }

        #endregion

        #region http

        private void Result(IAsyncResult ar)
        {
            //当接收到请求后程序流会走到这里

            var context = httpobj.EndGetContext(ar);

            //继续异步监听
            try
            {
                httpobj.BeginGetContext(Result, null);
            }
            catch (Exception ex)
            {
                ShowMsg(StringMsgType.Error, "Http", $"BeginGetContext Error：{ex.Message}");
            }

            var guid = Guid.NewGuid().ToString();
            ShowMsg(StringMsgType.Info, "Http", $"接到新的请求:{guid}.");
            //获得context对象
            var request = context.Request;
            var response = context.Response;
            ////如果是js的ajax请求，还可以设置跨域的ip地址与参数
            //context.Response.AppendHeader("Access-Control-Allow-Origin", "*");//后台跨域请求，通常设置为配置文件
            //context.Response.AppendHeader("Access-Control-Allow-Headers", "ID,PW");//后台跨域参数设置，通常设置为配置文件
            //context.Response.AppendHeader("Access-Control-Allow-Method", "post");//后台跨域请求设置，通常设置为配置文件
            context.Response.ContentType = "text/plain;charset=UTF-8";//告诉客户端返回的ContentType类型为纯文本格式，编码为UTF-8
            context.Response.AddHeader("Content-type", "text/plain");//添加响应头信息
            context.Response.ContentEncoding = Encoding.UTF8;
            string returnObj = "ERROR REQUEST";//定义返回客户端的信息
            if (request.HttpMethod == "POST" && request.InputStream != Stream.Null)
            {
                string tempurl = request.RawUrl.ToUpper();
                if (tempurl == "/TSPL" || tempurl == "/TSPL/")
                {
                    returnObj = HandlePostTSPL(request, response);
                }
                if (tempurl == "/FILE" || tempurl == "/FILE/")
                {
                    returnObj = HandlePostFile(request, response);
                }
                //if (tempurl == "/LABEL" || tempurl == "/LABEL/")
                //{
                //    returnObj = HandlePostLABEL(request, response);
                //}
            }
            if (request.HttpMethod == "GET")
            {
                string tempurl = request.RawUrl.ToUpper();
                if (tempurl == "/TEST" || tempurl == "/TEST/")
                {
                    returnObj = HandleTest(request, response);
                }
                else
                {
                    returnObj = HandleGet(request, response);
                }
            }
            var returnByteArr = Encoding.UTF8.GetBytes(returnObj);//设置客户端返回信息的编码
            try
            {
                using (var stream = response.OutputStream)
                {
                    //把处理信息返回到客户端
                    stream.Write(returnByteArr, 0, returnByteArr.Length);
                }
            }
            catch (Exception ex)
            {
                ShowMsg(StringMsgType.Error, "Http", $"网络异常：{ex.Message}");
            }
            ShowMsg(StringMsgType.Info, "Http", $"请求[{request.HttpMethod}][{request.RawUrl}]处理完成:{guid}.");
        }

        private string HandlePostTSPL(HttpListenerRequest request, HttpListenerResponse response)
        {
            string data = null;
            try
            {
                var byteList = new List<byte>();

                if (request.ContentType != null)
                {
                    if (request.ContentType.Contains("multipart/form-data"))
                    {
                        HttpMultipartParser parser = new HttpMultipartParser(request.InputStream, request.ContentEncoding, "");
                        if (parser.Success)
                        {
                            if (parser.Parameters.ContainsKey("tspl"))
                            {
                                data = parser.Parameters["tspl"];
                                byte[] byteArr = request.ContentEncoding.GetBytes(data);
                                byteList.AddRange(byteArr);
                            }
                            else
                            {
                                throw new Exception("Parser Content to TSPL Error");
                            }
                        }
                        else
                        {
                            throw new Exception("Parser Content to TSPL Error");
                        }
                    }
                    if (request.ContentType.Contains("text/plain"))
                    {
                        var byteArr = new byte[2048];
                        int readLen = 0;
                        int len = 0;
                        //接收客户端传过来的数据并转成字符串类型
                        do
                        {
                            readLen = request.InputStream.Read(byteArr, 0, byteArr.Length);
                            len += readLen;
                            byteList.AddRange(byteArr);
                        } while (readLen != 0);
                        data = Encoding.UTF8.GetString(byteList.ToArray(), 0, len);
                    }
                }

                string result = "";
                //获取得到数据data可以进行其他操作
                if (usbprint.openport())
                {
                    byte state = usbprint.printerstatus();
                    //ShowMsg(StringMsgType.Info, "Printer", "打印机状态=" + state.ToString());
                    if (state != 0)
                    {
                        ShowMsg(StringMsgType.Warning, "Printer", usbprint.printerstatus_string());
                        result = "ERROR, TSC printer not ready";
                        if (state == 2)
                        {
                            result = "ERROR, paper stuck in TSC Printer";
                        }
                        if (state == 4)
                        {
                            result = "ERROR, TSC Printer is out of paper";
                        }
                        response.StatusDescription = "404";
                        response.StatusCode = 404;
                        return result;
                    }
                    else
                    {
                        string[] lines = data.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        for(int i = 0;i < lines.Length; i++)
                        {
                            lines[i] += "\r\n";
                            //usbprint.sendcommand(lines[i]);
                            usbprint.sendcommand_utf8(lines[i]);
                        }

                        response.StatusDescription = "200";
                        response.StatusCode = 200;
                    }
                }
                else
                {
                    ShowMsg(StringMsgType.Warning, "Printer", "USB not open");
                    result = "ERROR, USB not open";
                    response.StatusDescription = "404";
                    response.StatusCode = 404;
                    return result;
                }
            }
            catch (Exception ex)
            {
                response.StatusDescription = "404";
                response.StatusCode = 404;
                ShowMsg(StringMsgType.Error, "Printer", $"发送TSPL失败:{ex.Message}.");
                return $"ERROR, {ex.Message}";
            }
            response.StatusDescription = "200";//获取或设置返回给客户端的 HTTP 状态代码的文本说明。
            response.StatusCode = 200;// 获取或设置返回给客户端的 HTTP 状态代码。
            ShowMsg(StringMsgType.Info, "Printer", $"发送TSPL完成:{data.Trim()}.");
            return "OK";
        }

        private string HandlePostFile(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                //转存文件
                Dictionary<string, string> args = new Dictionary<string, string>();
                RequestMultipartExtensions requestMultipartExtensions = new RequestMultipartExtensions();
                Dictionary<string, HttpFile> files = requestMultipartExtensions.ParseMultipartForm(request, args, (n, fn, ct) => new MemoryStream());
                string TempID = "";
                string JsonStr = "";
                foreach (var f in files.Values)
                {
                    f.Save(Path.Combine(TSCDirectory, f.FileName), true);
                    TempID = f.FileName;
                    ShowMsg(StringMsgType.Info, "Printer", $"下载标签[{TempID}]完成.");
                }
                if (args.ContainsKey("jsonstr"))
                {
                    JsonStr = args["jsonstr"];
                    ShowMsg(StringMsgType.Info, "Printer", $"接收参数完成： {JsonStr}.");
                }
                if (TempID != "")
                {
                    Dictionary<string, string> NewParms = JsonConvert.DeserializeObject<Dictionary<string, string>>(JsonStr);

                    string result = "";
                    //获取得到数据data可以进行其他操作
                    if (usbprint.openport())
                    {
                        byte state = usbprint.printerstatus();
                        //ShowMsg(StringMsgType.Info, "Printer", "打印机状态=" + state.ToString());
                        if (state != 0)
                        {
                            ShowMsg(StringMsgType.Warning, "Printer", usbprint.printerstatus_string());
                            result = "ERROR, TSC printer not ready";
                            if (state == 2)
                            {
                                result = "ERROR, paper stuck in TSC Printer";
                            }
                            if (state == 4)
                            {
                                result = "ERROR, TSC Printer is out of paper";
                            }
                            response.StatusDescription = "404";
                            response.StatusCode = 404;
                            return result;
                        }
                        else
                        {
                            //实例化一个对象
                            var btEngine = new Engine();
                            btEngine.Window.Visible = false;
                            //开始打印
                            btEngine.Start();
                            //打开模板
                            //var btFormat = btEngine.Documents.Open("D:\\文档1.btw");
                            var btFormat = btEngine.Documents.Open(Path.Combine(TSCDirectory, TempID));
                            //设置变量值(可选)
                            //btFormat.SubStrings["SubName"].Value = "1234";
                            if (NewParms != null)
                            {
                                foreach (string key in NewParms.Keys)
                                {
                                    btFormat.SubStrings[key].Value = NewParms[key];
                                }
                            }
                            //设置打印机名称
                            btFormat.PrintSetup.PrinterName = printer;
                            //设置打印张数
                            btFormat.PrintSetup.IdenticalCopiesOfLabel = 1;
                            //开始打印
                            Messages messages;
                            var pric = btFormat.Print("PrintingJobName", 10000, out messages);
                            //关闭文档
                            btFormat.Close(SaveOptions.DoNotSaveChanges);
                            //结束打印
                            btEngine.Stop();
                            //释放对象
                            btEngine.Dispose();

                            if (messages.HasError)
                            {
                                StringBuilder msb = new StringBuilder();
                                msb.Append("{");
                                for(int i = 0;i < messages.Count;i++)
                                {
                                    if(i > 0)
                                    {
                                        msb.Append(",");
                                    }
                                    msb.Append($"\"{messages[i].ID}\":\"{messages[i].Text}\"");
                                }
                                msb.Append("}");
                                throw new Exception(msb.ToString());
                            }
                            else
                            {
                                response.StatusDescription = "200";
                                response.StatusCode = 200;
                            }
                        }
                    }
                    else
                    {
                        ShowMsg(StringMsgType.Warning, "Printer", "USB not open");
                        result = "ERROR, USB not open";
                        response.StatusDescription = "404";
                        response.StatusCode = 404;
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                response.StatusDescription = "404";
                response.StatusCode = 404;
                ShowMsg(StringMsgType.Error, "Printer", $"打印标签失败:{ex.Message}.");
                return $"ERROR,{ex.Message}";
            }
            response.StatusDescription = "200";//获取或设置返回给客户端的 HTTP 状态代码的文本说明。
            response.StatusCode = 200;// 获取或设置返回给客户端的 HTTP 状态代码。
            ShowMsg(StringMsgType.Info, "Printer", $"打印标签完成.");
            return "OK";
        }

        private string HandlePostLABEL(HttpListenerRequest request, HttpListenerResponse response)
        {
            string TempID = "";
            string JsonStr = "";
            try
            {
                var byteList = new List<byte>();

                if (request.ContentType != null)
                {
                    if (request.ContentType.Contains("multipart/form-data"))
                    {
                        HttpMultipartParser parser = new HttpMultipartParser(request.InputStream, request.ContentEncoding, "");
                        if (parser.Success)
                        {
                            if (parser.Parameters.ContainsKey("tempid") && parser.Parameters.ContainsKey("jsonstr"))
                            {
                                TempID = parser.Parameters["tempid"];
                                JsonStr = parser.Parameters["jsonstr"];
                            }
                            else
                            {
                                throw new Exception("Parser Content to Label Error");
                            }
                        }
                        else
                        {
                            throw new Exception("Parser Content to TSPL Error");
                        }
                    }
                }

                Dictionary<string, string> NewParms = JsonConvert.DeserializeObject<Dictionary<string, string>>(JsonStr);
                string LabelPath = TSCDirectory + $"{TempID}.btw";

                string result = "";
                //获取得到数据data可以进行其他操作
                if (usbprint.openport())
                {
                    byte state = usbprint.printerstatus();
                    //ShowMsg(StringMsgType.Info, "Printer", "打印机状态=" + state.ToString());
                    if (state != 0)
                    {
                        ShowMsg(StringMsgType.Warning, "Printer", usbprint.printerstatus_string());
                        result = "ERROR, TSC printer not ready";
                        if (state == 2)
                        {
                            result = "ERROR, paper stuck in TSC Printer";
                        }
                        if (state == 4)
                        {
                            result = "ERROR, TSC Printer is out of paper";
                        }
                        response.StatusDescription = "404";
                        response.StatusCode = 404;
                        return result;
                    }
                    else
                    {
                        //实例化一个对象
                        var btEngine = new Engine();
                        btEngine.Window.Visible = false;
                        //开始打印
                        btEngine.Start();
                        //打开模板
                        //var btFormat = btEngine.Documents.Open("D:\\文档1.btw");
                        var btFormat = btEngine.Documents.Open(TempID);
                        //设置变量值(可选)
                        //btFormat.SubStrings["SubName"].Value = "1234";
                        foreach (string key in NewParms.Keys)
                        {
                            btFormat.SubStrings[key].Value = NewParms[key];
                        }
                        //设置打印机名称
                        btFormat.PrintSetup.PrinterName = printer;
                        //设置打印张数
                        btFormat.PrintSetup.IdenticalCopiesOfLabel = 1;
                        //开始打印
                        var pric = btFormat.Print("PrintingJobName");
                        //关闭文档
                        btFormat.Close(SaveOptions.DoNotSaveChanges);
                        //结束打印
                        btEngine.Stop();
                        //释放对象
                        btEngine.Dispose();

                        response.StatusDescription = "200";
                        response.StatusCode = 200;
                    }
                }
                else
                {
                    ShowMsg(StringMsgType.Warning, "Printer", "USB not open");
                    result = "ERROR, USB not open";
                    response.StatusDescription = "404";
                    response.StatusCode = 404;
                    return result;
                }
            }
            catch (Exception ex)
            {
                response.StatusDescription = "404";
                response.StatusCode = 404;
                ShowMsg(StringMsgType.Error, "Printer", $"打印标签[{TempID}]失败:{ex.Message}.");
                return $"ERROR, {ex.Message}";
            }
            response.StatusDescription = "200";//获取或设置返回给客户端的 HTTP 状态代码的文本说明。
            response.StatusCode = 200;// 获取或设置返回给客户端的 HTTP 状态代码。
            ShowMsg(StringMsgType.Info, "Printer", $"打印标签[{TempID}]完成, json: {JsonStr}.");
            return "OK";
        }

        private string HandleTest(HttpListenerRequest request, HttpListenerResponse response)
        {
            string result = "OK";
            try
            {
                if (usbprint.openport())
                {
                    byte state = usbprint.printerstatus();
                    ShowMsg(StringMsgType.Info, "Printer", "打印机状态=" + state.ToString());
                    if (state != 0)//(!driver.driver_status(printer))
                    {
                        //ShowMsg(StringMsgType.Warning, "Printer", "TSC Printer not ready");
                        ShowMsg(StringMsgType.Warning, "Printer", usbprint.printerstatus_string());
                        result = "Failed, error: TSC printer not ready";
                        if (state == 2)
                        {
                            result = "Failed, error: paper stuck in TSC Printer";
                        }
                        if (state == 4)
                        {
                            result = "Failed, error: TSC Printer is out of paper";
                        }
                        response.StatusDescription = "404";
                        response.StatusCode = 404;
                        return result;
                    }
                    else
                    {
                        usbprint.sendcommand("SIZE 60 mm,40 mm\r\n");
                        usbprint.sendcommand("GAP 2 mm\r\n");
                        usbprint.sendcommand("CLS\r\n");
                        usbprint.sendcommand("DIRECTION 1,0\r\n");
                        usbprint.sendcommand("TEXT 50,50,\"4\",0,1,1,\"looksword test\"\r\n");
                        usbprint.sendcommand("QRCODE 100,100,M,6,A,0,\"looksword test\"");
                        usbprint.sendcommand("PRINT 1\r\n");

                        //usbprint.sendcommand("SIZE 60 mm,40 mm\r\nGAP 2 mm\r\nCLS\r\nDIRECTION 1,0\r\nTEXT 50,50,\"4\",0,1,1,\"DEMO FOR TEXT\"\r\nPRINT 1\r\n");

                        response.StatusDescription = "200";
                        response.StatusCode = 200;
                        ShowMsg(StringMsgType.Info, "Printer", "test完成.");
                    }
                }
                else
                {
                    ShowMsg(StringMsgType.Warning, "Printer", "USB not open");
                    result = "Failed, error: USB not open";
                    response.StatusDescription = "404";
                    response.StatusCode = 404;
                    return result;
                }
            }
            catch (Exception e)
            {
                result = "Failed, error:" + e.Message;
                response.StatusDescription = "404";
                response.StatusCode = 404;
                ShowMsg(StringMsgType.Error, "Printer", e.Message);
            }
            finally
            {
                usbprint.closeport();
            }
            return result;
        }

        private string HandleGet(HttpListenerRequest request, HttpListenerResponse response)
        {
            StringBuilder ps = new StringBuilder();
            //ps.Append("{\"Printers\":\"");
            //ps.Append(printer);
            //ps.Append("\"}");
            TSCSDK.driver driver = new TSCSDK.driver();
            string[] drivers = driver.show_install_driver().Split(';');
            List<string> tsc_drivers = new List<string>();
            for (int i = 0; i < drivers.Length; i++)
            {
                if(drivers[i].ToUpper().Contains("TSC"))
                {
                    tsc_drivers.Add(drivers[i]);
                }
            }
            ps.Append("{\"TSC Printers\":[");
            for(int i = 0;i < tsc_drivers.Count;i++)
            {
                if (i > 0) ps.Append(",");
                ps.Append("\"");
                ps.Append(tsc_drivers[i]);
                ps.Append("\"");
            }
            //ps.Append("],");
            //ps.Append("\"Printers\":[");
            //for (int i = 0; i < drivers.Length; i++)
            //{
            //    if (i > 0) ps.Append(",");
            //    ps.Append("\"");
            //    ps.Append(drivers[i]);
            //    ps.Append("\"");
            //}
            ps.Append("]}");
            response.StatusDescription = "200";
            response.StatusCode = 200;
            return ps.ToString();
        }

        #endregion

        #region Printer

        private void PrintDetecter_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                TSCSDK.driver driver = new TSCSDK.driver();
                string[] drivers = driver.show_install_driver().Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> tsc_drivers = new List<string>();
                for (int i = 0; i < drivers.Length; i++)
                {
                    if (drivers[i].ToUpper().Contains("TSC"))
                    {
                        tsc_drivers.Add(drivers[i]);
                    }
                }
                if (tsc_drivers.Count > 0)
                {
                    printer = tsc_drivers[0];
                }
            }
            catch { }
            try
            {
                if (refreshListen > 120)
                {
                    refreshListen = 0;

                    //开启异步监听
                    try
                    {
                        httpobj.BeginGetContext(Result, null);
                    }
                    catch (Exception ex)
                    {
                        ShowMsg(StringMsgType.Error, "Http", $"BeginGetContext Error：{ex.Message}");
                    }
                }
            }
            catch
            {

            }
            refreshListen++;
        }

        #endregion
    }
}
