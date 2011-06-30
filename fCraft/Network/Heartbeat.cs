﻿// Copyright 2009, 2010, 2011 Matvei Stefarov <me@matvei.org>
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Text;
using fCraft.Events;

namespace fCraft {
    /// <summary> Static class responsible for sending heartbeats. </summary>
    public static class Heartbeat {
        const int HeartbeatDelay = 30000,
                  HeartbeatTimeout = 10000;
        public static string PrimaryUrl { get; set; }

        static HttpWebRequest request;
        static SchedulerTask task;
        static HeartbeatData data;

        /// <summary> Whether last attempt to send a heartbeat failed. </summary>
        public static bool LastHeartbeatFailed { get; private set; }


        static Heartbeat() {
            PrimaryUrl = "http://www.minecraft.net/heartbeat.jsp";
        }


        /// <summary> Callback for setting the local IP binding. Implements System.Net.BindIPEndPoint delegate. </summary>
        static IPEndPoint BindIPEndPointCallback( ServicePoint servicePoint, IPEndPoint remoteEndPoint, int retryCount ) {
            return new IPEndPoint( data.ServerIP, 0 );
        }


        /// <summary> Starts the heartbeats. </summary>
        public static void Start() {
            task = Scheduler.NewTask( Beat ).RunManual();
        }


        static void Beat( SchedulerTask scheduledTask ) {
            if( Server.IsShuttingDown ) return;

            data = new HeartbeatData {
                IsPublic = ConfigKey.IsPublic.Enabled(),
                MaxPlayers = ConfigKey.MaxPlayers.GetInt(),
                PlayerCount = Server.CountPlayers( false ),
                ServerIP = Server.IP,
                Port = Server.Port,
                ProtocolVersion = Config.ProtocolVersion,
                Salt = Server.Salt,
                ServerName = ConfigKey.ServerName.GetString()
            };

            // This needs to be wrapped in try/catch because and exception in an event handler
            // would permanently stop heartbeat sending.
            try {
                if( RaiseHeartbeatSendingEvent( data ) ) {
                    RescheduleHeartbeat();
                    return;
                }
            } catch( Exception ex ) {
                Logger.LogAndReportCrash( "Heartbeat.Sending handler failed", "fCraft", ex, false );
            }

            if( ConfigKey.HeartbeatEnabled.Enabled() ) {
                request = (HttpWebRequest)WebRequest.Create( PrimaryUrl );
                request.ServicePoint.BindIPEndPointDelegate = new BindIPEndPoint( BindIPEndPointCallback );
                request.Method = "POST";
                request.Timeout = HeartbeatTimeout;
                request.ContentType = "application/x-www-form-urlencoded";
                request.CachePolicy = new HttpRequestCachePolicy( HttpRequestCacheLevel.BypassCache );

                StringBuilder sb = new StringBuilder();
                sb.AppendFormat( "public={0}&max={1}&users={2}&port={3}&version={4}&salt={5}&name={6}",
                                 data.IsPublic,
                                 data.MaxPlayers,
                                 data.PlayerCount,
                                 data.Port,
                                 data.ProtocolVersion,
                                 Uri.EscapeDataString( data.Salt ),
                                 Uri.EscapeDataString( data.ServerName ) );

                foreach( var pair in data.CustomData ) {
                    sb.AppendFormat( "&{0}={1}",
                                     Uri.EscapeDataString( pair.Key ),
                                     Uri.EscapeDataString( pair.Value ) );
                }

                byte[] formData = Encoding.ASCII.GetBytes( sb.ToString() );
                request.ContentLength = formData.Length;

                request.BeginGetRequestStream( RequestCallback, formData );
            } else {
                // If heartbeats are disabled, the data is written to a text file (heartbeatdata.txt)
                const string tempFile = Paths.HeartbeatDataFileName + ".tmp";

                File.WriteAllLines( tempFile, new[]{
                        Server.Salt,
                        Server.IP.ToString(),
                        Server.Port.ToString(),
                        Server.CountPlayers(false).ToString(),
                        ConfigKey.MaxPlayers.GetString(),
                        ConfigKey.ServerName.GetString(),
                        ConfigKey.IsPublic.GetString()
                    }, Encoding.ASCII );

                Paths.MoveOrReplace( tempFile, Paths.HeartbeatDataFileName );
                RescheduleHeartbeat();
            }
        }


        static void RequestCallback( IAsyncResult result ) {
            if( Server.IsShuttingDown ) return;
            try {
                byte[] formData = (byte[])result.AsyncState;
                using( Stream requestStream = request.EndGetRequestStream( result ) ) {
                    requestStream.Write( formData, 0, formData.Length );
                }
                request.BeginGetResponse( ResponseCallback, null );
            } catch( Exception ex ) {
                LastHeartbeatFailed = true;
                if( ex is WebException || ex is IOException ) {
                    Logger.Log( "Heartbeat: Minecraft.net is probably down ({0})", LogType.Warning, ex.Message );
                } else {
                    Logger.Log( "Heartbeat: {0}", LogType.Error, ex );
                }
                RescheduleHeartbeat();
            }
        }


        static void ResponseCallback( IAsyncResult result ) {
            if( Server.IsShuttingDown ) return;
            try {
                string responseText;
                using( HttpWebResponse response = (HttpWebResponse)request.EndGetResponse( result ) ) {
                    using( StreamReader responseReader = new StreamReader( response.GetResponseStream() ) ) {
                        responseText = responseReader.ReadToEnd();
                    }
                    LastHeartbeatFailed = false;
                    RaiseHeartbeatSentEvent( data, response, responseText );
                }
                string newUrl = responseText.Trim();
                if( newUrl.StartsWith( "bad heartbeat", StringComparison.OrdinalIgnoreCase ) ) {
                    LastHeartbeatFailed = true;
                    Logger.Log( "Heartbeat: {0}", LogType.Error, newUrl );
                } else if( newUrl.Length > 32 && newUrl != Server.Url ) {
                    string oldUrl = Server.Url;
                    Server.Url = newUrl;
                    RaiseUrlChangedEvent( oldUrl, newUrl );
                }
            } catch( Exception ex ) {
                LastHeartbeatFailed = true;
                if( ex is WebException || ex is IOException ) {
                    Logger.Log( "Heartbeat: Minecraft.net is probably down ({0})", LogType.Warning, ex.Message );
                } else {
                    Logger.Log( "Heartbeat: {0}", LogType.Error, ex );
                }
            } finally {
                RescheduleHeartbeat();
            }
        }


        static void RescheduleHeartbeat() {
            task.RunManual( TimeSpan.FromMilliseconds( HeartbeatDelay ) );
        }


        #region Events

        /// <summary> Occurs when a heartbeat is about to be sent (cancellable). </summary>
        public static event EventHandler<HeartbeatSendingEventArgs> Sending;

        /// <summary> Occurs when a heartbeat has been sent. </summary>
        public static event EventHandler<HeartbeatSentEventArgs> Sent;

        /// <summary> Occurs when the server Url has been set or changed. </summary>
        public static event EventHandler<UrlChangedEventArgs> UrlChanged;


        static bool RaiseHeartbeatSendingEvent( HeartbeatData heartbeatData ) {
            var h = Sending;
            if( h == null ) return false;
            var e = new HeartbeatSendingEventArgs( heartbeatData );
            h( null, e );
            return e.Cancel;
        }

        static void RaiseHeartbeatSentEvent( HeartbeatData heartbeatData,
                                             HttpWebResponse response,
                                             string text ) {
            var h = Sent;
            if( h != null ) {
                h( null, new HeartbeatSentEventArgs( heartbeatData,
                                                     response.Headers,
                                                     response.StatusCode,
                                                     text ) );
            }
        }

        static void RaiseUrlChangedEvent( string oldUrl, string newUrl ) {
            var h = UrlChanged;
            if( h != null ) h( null, new UrlChangedEventArgs( oldUrl, newUrl ) );
        }

        #endregion
    }


    public sealed class HeartbeatData {
        public HeartbeatData() {
            CustomData = new Dictionary<string, string>();
        }
        public string Salt { get; set; }
        public IPAddress ServerIP { get; set; }
        public int Port { get; set; }
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public string ServerName { get; set; }
        public bool IsPublic { get; set; }
        public int ProtocolVersion { get; set; }
        public Dictionary<string, string> CustomData { get; private set; }
    }
}


namespace fCraft.Events {

    public sealed class HeartbeatSentEventArgs : EventArgs {
        internal HeartbeatSentEventArgs( HeartbeatData heartbeatData,
                                         WebHeaderCollection headers,
                                         HttpStatusCode status, 
                                         string text ) {
            HeartbeatData = heartbeatData;
            ResponseHeaders = headers;
            ResponseStatusCode = status;
            ResponseText = text;
        }
        public HeartbeatData HeartbeatData { get; private set; }
        public WebHeaderCollection ResponseHeaders { get; private set; }
        public HttpStatusCode ResponseStatusCode { get; private set; }
        public string ResponseText { get; private set; }
    }


    public sealed class HeartbeatSendingEventArgs : EventArgs, ICancellableEvent {
        internal HeartbeatSendingEventArgs( HeartbeatData data ) {
            HeartbeatData = data;
        }
        public bool Cancel { get; set; }
        public HeartbeatData HeartbeatData { get; private set; }
    }


    public sealed class UrlChangedEventArgs : EventArgs {
        internal UrlChangedEventArgs( string oldUrl, string newUrl ) {
            OldUrl = oldUrl;
            NewUrl = newUrl;
        }
        public string OldUrl { get; private set; }
        public string NewUrl { get; private set; }
    }

}