using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LabelHttpServer
{
    public delegate void HandleStringMsg(StringMsgType msgType, string msg);
    /// <summary>
    /// 日志类型
    /// </summary>
    public enum StringMsgType
    {
        Data = 0,
        Error,
        Info,
        Mark,
        Warning
    }

    /// <summary>
    /// multipart/form-data的解析器
    /// </summary>
    internal class HttpMultipartParser
    {
        /// <summary>
        /// 参数集合
        /// </summary>
        public IDictionary<string, string> Parameters = new Dictionary<string, string>();
        /// <summary>
        /// 上传文件部分参数
        /// </summary>
        public string FilePartName { get; }
        /// <summary>
        /// 是否解析成功
        /// </summary>
        public bool Success { get; private set; }
        /// <summary>
        /// 请求类型
        /// </summary>
        public string ContentType { get; private set; }
        /// <summary>
        /// 上传的文件名
        /// </summary>
        public string Filename { get; private set; }
        /// <summary>
        /// 上传的文件内容
        /// </summary>
        public List<byte[]> FileContents { get; private set; }

        /// <summary>
        /// 解析multipart/form-data格式的文件请求，默认编码为utf8
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="filePartName"></param>
        public HttpMultipartParser(Stream stream, string filePartName)
        {
            FileContents = new List<byte[]>();
            FilePartName = filePartName;
            Parse(stream, Encoding.UTF8);
        }

        /// <summary>
        /// 解析multipart/form-data格式的字符串
        /// </summary>
        /// <param name="content"></param>
        public HttpMultipartParser(string content)
        {
            FileContents = new List<byte[]>();
            var array = Encoding.UTF8.GetBytes(content);
            var stream = new MemoryStream(array);
            Parse(stream, Encoding.UTF8);
        }

        /// <summary>
        /// 解析multipart/form-data格式的文件请求
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="encoding">编码</param>
        /// <param name="filePartName"></param>
        public HttpMultipartParser(Stream stream, Encoding encoding, string filePartName)
        {
            FileContents = new List<byte[]>();
            FilePartName = filePartName;
            Parse(stream, encoding);
        }

        private void Parse(Stream stream, Encoding encoding)
        {
            Success = false;

            var data = ToByteArray(stream);

            var content = encoding.GetString(data);

            var delimiterEndIndex = content.IndexOf("\r\n", StringComparison.Ordinal);

            if (delimiterEndIndex > -1)
            {
                var delimiter = content.Substring(0, content.IndexOf("\r\n", StringComparison.Ordinal)).Trim();

                var sections = content.Split(new[] { delimiter }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var s in sections)
                {
                    if (s.Contains("Content-Disposition"))
                    {
                        var nameMatch = new Regex(@"(?<=name\=\"")(.*?)(?=\"")").Match(s);
                        string name = nameMatch.Value.Trim().ToLower();

                        if (name == FilePartName && !string.IsNullOrEmpty(FilePartName))
                        {
                            var re = new Regex(@"(?<=Content\-Type:)(.*?)(?=\r\n\r\n)");
                            var contentTypeMatch = re.Match(s);

                            re = new Regex(@"(?<=filename\=\"")(.*?)(?=\"")");
                            var filenameMatch = re.Match(s);

                            if (contentTypeMatch.Success && filenameMatch.Success)
                            {
                                ContentType = contentTypeMatch.Value.Trim();
                                Filename = filenameMatch.Value.Trim();

                                int startIndex = contentTypeMatch.Index + contentTypeMatch.Length + "\r\n\r\n".Length;

                                string tempfilestr = s.Substring(startIndex);

                                int templen = tempfilestr.Length;
                                tempfilestr = tempfilestr.Remove(templen - 2);//去除默认\r\n

                                byte[] fileData = encoding.GetBytes(tempfilestr);

                                //var delimiterBytes = encoding.GetBytes("\r\n" + delimiter);
                                //var endIndex = IndexOf(data, delimiterBytes, startIndex);

                                //var contentLength = endIndex - startIndex;

                                //var fileData = new byte[contentLength];

                                //Buffer.BlockCopy(data, startIndex, fileData, 0, contentLength);

                                FileContents.Add(fileData);
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(name))
                        {
                            var startIndex = nameMatch.Index + nameMatch.Length + "\r\n\r\n".Length;
                            Parameters.Add(name, s.Substring(startIndex).TrimEnd('\r', '\n').Trim());
                        }
                    }
                }

                if (FileContents.Count != 0 || Parameters.Count != 0)
                {
                    Success = true;
                }
            }
        }

        public static int IndexOf(byte[] searchWithin, byte[] serachFor, int startIndex)
        {
            var index = 0;
            var startPos = Array.IndexOf(searchWithin, serachFor[0], startIndex);

            if (startPos != -1)
            {
                while (startPos + index < searchWithin.Length)
                {
                    if (searchWithin[startPos + index] == serachFor[index])
                    {
                        index++;
                        if (index == serachFor.Length)
                        {
                            return startPos;
                        }
                    }
                    else
                    {
                        startPos = Array.IndexOf(searchWithin, serachFor[0], startPos + index);
                        if (startPos == -1)
                        {
                            return -1;
                        }

                        index = 0;
                    }
                }
            }

            return -1;
        }

        public static byte[] ToByteArray(Stream stream)
        {
            var buffer = new byte[32768];
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        return ms.ToArray();
                    }

                    ms.Write(buffer, 0, read);
                }
            }
        }
    }
}
