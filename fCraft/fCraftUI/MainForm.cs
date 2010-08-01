﻿// Copyright 2009, 2010 Matvei Stefarov <me@matvei.org>
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using System.Diagnostics;
using fCraft;


namespace fCraftUI {
    public partial class MainForm : Form {
        bool shuttingDown = false;
        string[] args;
        const int MaxLinesInLog = 2000;

        public MainForm( string[] _args ) {
            args = _args;
            InitializeComponent();
            Shown += StartUp;
            FormClosing += HandleShutDown;
            console.OnCommand += console_Enter;
        }


        void StartUp( object sender, EventArgs a ) {
            Server.OnLog += Log;
            Server.OnURLChanged += SetURL;
            Server.OnPlayerListChanged += UpdatePlayerList; //TODO

            try {
                if( Server.Init() ) {
                    Text = "fCraft " + Updater.GetVersionString() + " - " + Config.GetString( ConfigKey.ServerName );

                    UpdaterResult update = Updater.CheckForUpdates();
                    if( update.UpdateAvailable ) {
                        if( Config.GetString( ConfigKey.AutomaticUpdates ) == "Notify" ) {
                            Log( String.Format( Environment.NewLine +
                                                "*** A new version of fCraft is available: v{0}, released {1:0} day(s) ago. ***" +
                                                Environment.NewLine,
                                                update.GetVersionString(),
                                                DateTime.Now.Subtract( update.ReleaseDate ).TotalDays ), LogType.ConsoleOutput );
                            StartServer();
                        } else {
                            UpdateWindow updateWindow = new UpdateWindow( update, this, Config.GetString( ConfigKey.AutomaticUpdates ) == "Auto" );
                            updateWindow.StartPosition = FormStartPosition.CenterParent;
                            updateWindow.ShowDialog();
                        }
                    } else {
                        StartServer();
                    }
                } else {
                    Logger.Log( "---- Could Not Initialize Server ----", LogType.FatalError );
                }
            } catch( Exception ex ) {
                MessageBox.Show( ex.ToString(), "fCraft crashed!" );
                System.IO.File.WriteAllText( "crash.log", ex.ToString() + Environment.NewLine + ex.StackTrace );
            }
        }


        public void StartServer() {
            Process.GetCurrentProcess().PriorityClass = Config.GetProcessPriority();
            if( Server.Start() ) {
                console.Enabled = true;
            } else {
                Logger.Log( "---- Could Not Start The Server ----", LogType.FatalError );
            }
        }

        void HandleShutDown( object sender, CancelEventArgs e ) {
            shuttingDown = true;
            Server.Shutdown();
        }


        delegate void LogDelegate( string message );
        delegate void PlayerListUpdateDelegate( string[] items );

        public void Log( string message, LogType type ) {
            if( shuttingDown ) return;
            if( logBox.InvokeRequired ) {
                LogDelegate d = new LogDelegate( LogInternal );
                try {
                    Invoke( d, new object[] { message } );
                } catch { };
            } else {
                LogInternal( message );
            }
        }

        void LogInternal( string message ) {
            logBox.AppendText( message + Environment.NewLine );
            if( logBox.Lines.Length > MaxLinesInLog ) {
                logBox.Text = "----- cut off, see fCraft.log for complete log -----" +
                    Environment.NewLine +
                    logBox.Text.Substring( logBox.GetFirstCharIndexFromLine( 50 ) );
            }
            logBox.SelectionStart = logBox.Text.Length;
            logBox.ScrollToCaret();
        }


        public void SetURL( string URL ) {
            if( urlDisplay.InvokeRequired ) {
                LogDelegate d = new LogDelegate( SetURLInternal );
                Invoke( d, new object[] { URL } );
            } else {
                SetURLInternal( URL );
            }
        }

        void SetURLInternal( string URL ) {
            urlDisplay.Text = URL;
            urlDisplay.Enabled = true;
            urlDisplay.Select();
        }


        public void UpdatePlayerList( string[] playerNames ) {
            if( playerList.InvokeRequired ) {
                PlayerListUpdateDelegate d = new PlayerListUpdateDelegate( UpdatePlayerListInternal );
                Invoke( d, new object[] { playerNames } );
            } else {
                UpdatePlayerListInternal( playerNames );
            }
        }

        void UpdatePlayerListInternal( string[] items ) {
            playerList.Items.Clear();
            Array.Sort( items );
            foreach( string item in items ) {
                playerList.Items.Add( item );
            }
        }


        private void console_Enter() {
            string[] separator = { Environment.NewLine };
            string[] lines = console.Text.Trim().Split( separator, StringSplitOptions.RemoveEmptyEntries );
            foreach( string line in lines ) {
                try {
                    Player.Console.ParseMessage( line, true );
                } catch( Exception ex ) {
                    Logger.LogConsole( "Error occured while trying to execute last console command: " );
                    Logger.LogConsole( ex.ToString() + ": " + ex.Message );
                }
            }
            console.Text = "";
        }
    }
}