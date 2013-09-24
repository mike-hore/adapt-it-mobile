/*  
	Licensed under the Apache License, Version 2.0 (the "License");
	you may not use this file except in compliance with the License.
	You may obtain a copy of the License at
	
	http://www.apache.org/licenses/LICENSE-2.0
	
	Unless required by applicable law or agreed to in writing, software
	distributed under the License is distributed on an "AS IS" BASIS,
	WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
	See the License for the specific language governing permissions and
	limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Net;
using System.Runtime.Serialization;
using System.Windows;
using System.Security;
using System.Diagnostics;

namespace WPCordovaClassLib.Cordova.Commands
{
    public class FileTransfer : BaseCommand
    {
        public class DownloadRequestState
        {
            // This class stores the State of the request.
            public HttpWebRequest request;
            public TransferOptions options;

            public DownloadRequestState()
            {
                request = null;
                options = null;
            }
        }


        public class TransferOptions
        {
            /// File path to upload  OR File path to download to
            public string FilePath { get; set; }

            public string Url { get; set; }
            /// Flag to recognize if we should trust every host (only in debug environments)
            public bool TrustAllHosts { get; set; }
            public string Id { get; set; }
            public string Headers { get; set; }
            public string CallbackId { get; set; }
            public bool ChunkedMode { get; set; }
            /// Server address
            public string Server { get; set; }
            /// File key
            public string FileKey { get; set; }
            /// File name on the server
            public string FileName { get; set; }
            /// File Mime type
            public string MimeType { get; set; }
            /// Additional options
            public string Params { get; set; }
            public string Method { get; set; }

            public TransferOptions()
            {
                FileKey = "file";
                FileName = "image.jpg";
                MimeType = "image/jpeg";
            }
        }

        /// <summary>
        /// Boundary symbol
        /// </summary>       
        private string Boundary = "----------------------------" + DateTime.Now.Ticks.ToString("x");

        // Error codes
        public const int FileNotFoundError = 1;
        public const int InvalidUrlError = 2;
        public const int ConnectionError = 3;
        public const int AbortError = 4; // not really an error, but whatevs

        private static Dictionary<string, DownloadRequestState> InProcDownloads = new Dictionary<string,DownloadRequestState>();


        /// <summary>
        /// Uploading response info
        /// </summary>
        [DataContract]
        public class FileUploadResult
        {
            /// <summary>
            /// Amount of sent bytes
            /// </summary>
            [DataMember(Name = "bytesSent")]
            public long BytesSent { get; set; }

            /// <summary>
            /// Server response code
            /// </summary>
            [DataMember(Name = "responseCode")]
            public long ResponseCode { get; set; }

            /// <summary>
            /// Server response
            /// </summary>
            [DataMember(Name = "response", EmitDefaultValue = false)]
            public string Response { get; set; }

            /// <summary>
            /// Creates FileUploadResult object with response values
            /// </summary>
            /// <param name="bytesSent">Amount of sent bytes</param>
            /// <param name="responseCode">Server response code</param>
            /// <param name="response">Server response</param>
            public FileUploadResult(long bytesSent, long responseCode, string response)
            {
                this.BytesSent = bytesSent;
                this.ResponseCode = responseCode;
                this.Response = response;
            }
        }
        /// <summary>
        /// Represents transfer error codes for callback
        /// </summary>
        [DataContract]
        public class FileTransferError
        {
            /// <summary>
            /// Error code
            /// </summary>
            [DataMember(Name = "code", IsRequired = true)]
            public int Code { get; set; }

            /// <summary>
            /// The source URI
            /// </summary>
            [DataMember(Name = "source", IsRequired = true)]
            public string Source { get; set; }

            /// <summary>
            /// The target URI
            /// </summary>
            /// 
            [DataMember(Name = "target", IsRequired = true)]
            public string Target { get; set; }

            [DataMember(Name = "body", IsRequired = true)]
            public string Body { get; set; }

            /// <summary>
            /// The http status code response from the remote URI
            /// </summary>
            [DataMember(Name = "http_status", IsRequired = true)]
            public int HttpStatus { get; set; }

            /// <summary>
            /// Creates FileTransferError object
            /// </summary>
            /// <param name="errorCode">Error code</param>
            public FileTransferError(int errorCode)
            {
                this.Code = errorCode;
                this.Source = null;
                this.Target = null;
                this.HttpStatus = 0;
                this.Body = "";
            }
            public FileTransferError(int errorCode, string source, string target, int status, string body = "")
            {
                this.Code = errorCode;
                this.Source = source;
                this.Target = target;
                this.HttpStatus = status;
                this.Body = body;
            }
        }

        /// <summary>
        /// Upload options
        /// </summary>
        //private TransferOptions uploadOptions;

        /// <summary>
        /// Bytes sent
        /// </summary>
        private long bytesSent;

        /// <summary>
        /// sends a file to a server
        /// </summary>
        /// <param name="options">Upload options</param>
        /// exec(win, fail, 'FileTransfer', 'upload', [filePath, server, fileKey, fileName, mimeType, params, trustAllHosts, chunkedMode, headers, this._id, httpMethod]);
        public void upload(string options)
        {
            options = options.Replace("{}", ""); // empty objects screw up the Deserializer
            string callbackId = "";

            TransferOptions uploadOptions = null;
            HttpWebRequest webRequest = null;

            try 
            {
                try 
                {
                    string[] args = JSON.JsonHelper.Deserialize<string[]>(options);
                    uploadOptions = new TransferOptions();
                    uploadOptions.FilePath = args[0];
                    uploadOptions.Server = args[1];
                    uploadOptions.FileKey = args[2];
                    uploadOptions.FileName = args[3];
                    uploadOptions.MimeType = args[4];
                    uploadOptions.Params = args[5];


                    bool trustAll = false;
                    bool.TryParse(args[6],out trustAll);
                    uploadOptions.TrustAllHosts = trustAll;

                    bool doChunked = false;
                    bool.TryParse(args[7], out doChunked);
                    uploadOptions.ChunkedMode = doChunked;

                    //8 : Headers
                    //9 : id
                    //10: method

                    uploadOptions.Headers = args[8];
                    uploadOptions.Id = args[9];
                    uploadOptions.Method = args[10];

                    uploadOptions.CallbackId = callbackId = args[11];
                }
                catch (Exception)
                {
                    DispatchCommandResult(new PluginResult(PluginResult.Status.JSON_EXCEPTION));
                    return;
                }

                Uri serverUri;
                try
                {
                    serverUri = new Uri(uploadOptions.Server);
                }
                catch (Exception)
                {
                    DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, new FileTransferError(InvalidUrlError, uploadOptions.Server, null, 0)));
                    return;
                }
                webRequest = (HttpWebRequest)WebRequest.Create(serverUri);
                webRequest.ContentType = "multipart/form-data;boundary=" + Boundary;
                webRequest.Method = uploadOptions.Method;

                if (!string.IsNullOrEmpty(uploadOptions.Headers))
                {
                    Dictionary<string, string> headers = parseHeaders(uploadOptions.Headers);
                    foreach (string key in headers.Keys)
                    {
                        webRequest.Headers[key] = headers[key];
                    }
                }

                DownloadRequestState reqState = new DownloadRequestState();
                reqState.options = uploadOptions;
                reqState.request = webRequest;

                InProcDownloads[uploadOptions.Id] = reqState;


                webRequest.BeginGetRequestStream(WriteCallback, reqState);
            }
            catch (Exception ex)
            {

                DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, new FileTransferError(ConnectionError)),callbackId);

                
            }
        }

        // example : "{\"Authorization\":\"Basic Y29yZG92YV91c2VyOmNvcmRvdmFfcGFzc3dvcmQ=\"}"
        protected Dictionary<string,string> parseHeaders(string jsonHeaders) 
        {
            Dictionary<string, string> result = new Dictionary<string, string>();

            string temp = jsonHeaders.StartsWith("{") ? jsonHeaders.Substring(1) : jsonHeaders;
            temp = temp.EndsWith("}") ? temp.Substring(0,temp.Length - 1) :  temp;
            // "\"Authorization\":\"Basic Y29yZG92YV91c2VyOmNvcmRvdmFfcGFzc3dvcmQ=\""

            string[] strHeaders = temp.Split(',');
            int count = 2;
            char[] delimiter = ":".ToCharArray();
            for (int n = 0; n < strHeaders.Length; n++)
            {
                string[] split = strHeaders[n].Split(delimiter, System.StringSplitOptions.RemoveEmptyEntries);
                if (split.Length == 2)
                {
                    split[0] = JSON.JsonHelper.Deserialize<string>(split[0]);
                    split[1] = JSON.JsonHelper.Deserialize<string>(split[1]);
                    result[split[0]] = split[1];
                }
            }
            return result;
        }

        public void download(string options)
        {
            TransferOptions downloadOptions = null;
            HttpWebRequest webRequest = null;
            string callbackId;

            try
            {
                string[] optionStrings = JSON.JsonHelper.Deserialize<string[]>(options);

                downloadOptions = new TransferOptions();
                downloadOptions.Url = optionStrings[0];
                downloadOptions.FilePath = optionStrings[1];

                bool trustAll = false;
                bool.TryParse(optionStrings[2],out trustAll);
                downloadOptions.TrustAllHosts = trustAll;

                downloadOptions.Id = optionStrings[3];
                downloadOptions.Headers = optionStrings[4];
                downloadOptions.CallbackId = callbackId = optionStrings[5];

            }
            catch (Exception)
            {
                DispatchCommandResult(new PluginResult(PluginResult.Status.JSON_EXCEPTION));
                return;
            }

            try
            {
                Debug.WriteLine("Creating WebRequest for url : " + downloadOptions.Url);
                webRequest = (HttpWebRequest)WebRequest.Create(downloadOptions.Url);
            }
            //catch (WebException webEx)
            //{

            //}
            catch (Exception)
            {
                DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, new FileTransferError(InvalidUrlError, downloadOptions.Url, null, 0)));
                return;
            }

            if (downloadOptions != null && webRequest != null)
            {
                DownloadRequestState state = new DownloadRequestState();
                state.options = downloadOptions;
                state.request = webRequest;
                InProcDownloads[downloadOptions.Id] = state;

                if (!string.IsNullOrEmpty(downloadOptions.Headers))
                {
                    Dictionary<string, string> headers = parseHeaders(downloadOptions.Headers);
                    foreach (string key in headers.Keys)
                    {
                        webRequest.Headers[key] = headers[key];
                    }
                }
                
                webRequest.BeginGetResponse(new AsyncCallback(downloadCallback), state);
            }



        }

        public void abort(string options)
        {
            string[] optionStrings = JSON.JsonHelper.Deserialize<string[]>(options);
            string id = optionStrings[0];
            string callbackId = optionStrings[1];  

            if (InProcDownloads.ContainsKey(id))
            {
                 DownloadRequestState state = InProcDownloads[id];
                 state.request.Abort();
                 state.request = null;
                 
                 callbackId = state.options.CallbackId;
                 InProcDownloads.Remove(id);
                 state = null;
                 DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, new FileTransferError(FileTransfer.AbortError)),
                       callbackId);

            }
            else
            {
                DispatchCommandResult(new PluginResult(PluginResult.Status.IO_EXCEPTION), callbackId); // TODO: is it an IO exception?
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="asynchronousResult"></param>
        private void downloadCallback(IAsyncResult asynchronousResult)
        {
            DownloadRequestState reqState = (DownloadRequestState)asynchronousResult.AsyncState;
            HttpWebRequest request = reqState.request;

            string callbackId = reqState.options.CallbackId;

            if (InProcDownloads.ContainsKey(reqState.options.Id))
            {
                InProcDownloads.Remove(reqState.options.Id);
            }

            try
            {
                HttpWebResponse response = (HttpWebResponse)request.EndGetResponse(asynchronousResult);

                using (IsolatedStorageFile isoFile = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    // create the file if not exists
                    if (!isoFile.FileExists(reqState.options.FilePath))
                    {
                        var file = isoFile.CreateFile(reqState.options.FilePath);
                        file.Close();
                    }

                    using (FileStream fileStream = new IsolatedStorageFileStream(reqState.options.FilePath, FileMode.Open, FileAccess.Write, isoFile))
                    {
                        long totalBytes = response.ContentLength;
                        int bytesRead = 0;
                        using (BinaryReader reader = new BinaryReader(response.GetResponseStream()))
                        {
                            using (BinaryWriter writer = new BinaryWriter(fileStream))
                            {
                                int BUFFER_SIZE = 1024;
                                byte[] buffer;

                                while (true)
                                {
                                    buffer = reader.ReadBytes(BUFFER_SIZE);
                                    // fire a progress event ?
                                    bytesRead += buffer.Length;
                                    if (buffer.Length > 0)
                                    {
                                        writer.Write(buffer);
                                    }
                                    else
                                    {
                                        writer.Close();
                                        reader.Close();
                                        fileStream.Close();
                                        break;
                                    }
                                }
                            }

                        }


                    }
                }
                File.FileEntry entry = new File.FileEntry(reqState.options.FilePath);
                DispatchCommandResult(new PluginResult(PluginResult.Status.OK, entry), callbackId);
            }
            catch (IsolatedStorageException)
            {
                // Trying to write the file somewhere within the IsoStorage.
                DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, new FileTransferError(FileNotFoundError)),
                                      callbackId);
            }
            catch (SecurityException)
            {
                // Trying to write the file somewhere not allowed.
                DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, new FileTransferError(FileNotFoundError)),
                                      callbackId);
            }
            catch (WebException webex)
            {
                // TODO: probably need better work here to properly respond with all http status codes back to JS
                // Right now am jumping through hoops just to detect 404.
                HttpWebResponse response = (HttpWebResponse)webex.Response;
                if ((webex.Status == WebExceptionStatus.ProtocolError && response.StatusCode == HttpStatusCode.NotFound) 
                    || webex.Status == WebExceptionStatus.UnknownError)
                {
                    
                    // Weird MSFT detection of 404... seriously... just give us the f(*&#$@ status code as a number ffs!!!
                    // "Numbers for HTTP status codes? Nah.... let's create our own set of enums/structs to abstract that stuff away."
                    // FACEPALM
                    // Or just cast it to an int, whiner ... -jm
                    int statusCode = (int)response.StatusCode;
                    string body = "";

                    using (Stream streamResponse = response.GetResponseStream())
                    {
                        using (StreamReader streamReader = new StreamReader(streamResponse))
                        {
                            body = streamReader.ReadToEnd();
                        }
                    }
                    FileTransferError ftError = new FileTransferError(ConnectionError, null, null, statusCode, body);
                    DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, ftError), 
                                          callbackId);
                }
                else
                {
                    DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, 
                                                           new FileTransferError(ConnectionError)),
                                          callbackId);
                }
            }
            catch (Exception)
            {
                DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, 
                                                        new FileTransferError(FileNotFoundError)),
                                      callbackId);
            }
        }



        /// <summary>
        /// Read file from Isolated Storage and sends it to server
        /// </summary>
        /// <param name="asynchronousResult"></param>
        private void WriteCallback(IAsyncResult asynchronousResult)
        {
            DownloadRequestState reqState = (DownloadRequestState)asynchronousResult.AsyncState;
            HttpWebRequest webRequest = reqState.request;
            string callbackId = reqState.options.CallbackId;

            try
            {
                using (Stream requestStream = (webRequest.EndGetRequestStream(asynchronousResult)))
                {
                    string lineStart = "--";
                    string lineEnd = Environment.NewLine;
                    byte[] boundaryBytes = System.Text.Encoding.UTF8.GetBytes(lineStart + Boundary + lineEnd);
                    string formdataTemplate = "Content-Disposition: form-data; name=\"{0}\"" + lineEnd + lineEnd + "{1}" + lineEnd;

                    if (!string.IsNullOrEmpty(reqState.options.Params))
                    {
                        Dictionary<string, string> paramMap = parseHeaders(reqState.options.Params);
                        foreach (string key in paramMap.Keys)
                        {
                            requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                            string formItem = string.Format(formdataTemplate, key, paramMap[key]);
                            byte[] formItemBytes = System.Text.Encoding.UTF8.GetBytes(formItem);
                            requestStream.Write(formItemBytes, 0, formItemBytes.Length);
                        }
                        requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                    }
                    using (IsolatedStorageFile isoFile = IsolatedStorageFile.GetUserStoreForApplication())
                    {
                        if (!isoFile.FileExists(reqState.options.FilePath))
                        {
                            DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, new FileTransferError(FileNotFoundError, reqState.options.Server, reqState.options.FilePath, 0)));
                            return;
                        }

                        using (FileStream fileStream = new IsolatedStorageFileStream(reqState.options.FilePath, FileMode.Open, isoFile))
                        {
                            string headerTemplate = "Content-Disposition: form-data; name=\"{0}\"; filename=\"{1}\"" + lineEnd + "Content-Type: {2}" + lineEnd + lineEnd;
                            string header = string.Format(headerTemplate, reqState.options.FileKey, reqState.options.FileName, reqState.options.MimeType);
                            byte[] headerBytes = System.Text.Encoding.UTF8.GetBytes(header);
                            requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                            requestStream.Write(headerBytes, 0, headerBytes.Length);
                            byte[] buffer = new byte[4096];
                            int bytesRead = 0;

                            while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                // TODO: Progress event
                                requestStream.Write(buffer, 0, bytesRead);
                                bytesSent += bytesRead;
                            }
                        }
                        byte[] endRequest = System.Text.Encoding.UTF8.GetBytes(lineEnd + lineStart + Boundary + lineStart + lineEnd);
                        requestStream.Write(endRequest, 0, endRequest.Length);
                    }
                }
                // webRequest

                webRequest.BeginGetResponse(ReadCallback, reqState);
            }
            catch (Exception ex)
            {
                DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, new FileTransferError(ConnectionError)),callbackId);
            }
        }

        /// <summary>
        /// Reads response into FileUploadResult
        /// </summary>
        /// <param name="asynchronousResult"></param>
        private void ReadCallback(IAsyncResult asynchronousResult)
        {
            DownloadRequestState reqState = (DownloadRequestState)asynchronousResult.AsyncState;
            try
            {
                HttpWebRequest webRequest = reqState.request;
                string callbackId = reqState.options.CallbackId;

                if (InProcDownloads.ContainsKey(reqState.options.Id))
                {
                    InProcDownloads.Remove(reqState.options.Id);
                }

                using (HttpWebResponse response = (HttpWebResponse)webRequest.EndGetResponse(asynchronousResult))
                {
                    using (Stream streamResponse = response.GetResponseStream())
                    {
                        using (StreamReader streamReader = new StreamReader(streamResponse))
                        {
                            string responseString = streamReader.ReadToEnd();
                            Deployment.Current.Dispatcher.BeginInvoke(() =>
                            {
                                DispatchCommandResult(new PluginResult(PluginResult.Status.OK, new FileUploadResult(bytesSent, (long)response.StatusCode, responseString)));
                            });
                        }
                    }
                }
            }
            catch (WebException webex)
            {
                // TODO: probably need better work here to properly respond with all http status codes back to JS
                // Right now am jumping through hoops just to detect 404.
                if ((webex.Status == WebExceptionStatus.ProtocolError && ((HttpWebResponse)webex.Response).StatusCode == HttpStatusCode.NotFound)
                    || webex.Status == WebExceptionStatus.UnknownError)
                {
                    int statusCode = (int)((HttpWebResponse)webex.Response).StatusCode;
                    FileTransferError ftError = new FileTransferError(ConnectionError, null, null, statusCode);
                    DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, ftError), reqState.options.CallbackId);
                }
                else
                {
                    DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR,
                                                           new FileTransferError(ConnectionError)),
                                          reqState.options.CallbackId);
                }
            }
            catch (Exception ex)
            {
                FileTransferError transferError = new FileTransferError(ConnectionError, reqState.options.Server, reqState.options.FilePath, 403);
                DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, transferError), reqState.options.CallbackId);

            }
        }
    }
}