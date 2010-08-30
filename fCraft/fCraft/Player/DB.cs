﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Data;
using System.Data.SQLite;
using System.Net;


namespace fCraft {
    static class DB {
        static SQLiteConnection db;
        const string DatabaseFile = "fCraft.db";
        const int SchemaVersion = 1;
        static SQLiteCommand cmd_PlayerInfo_ProcessLogin,
                             cmd_PlayerInfo_ProcessLogout,
                             cmd_PlayerInfo_ProcessBan,
                             cmd_PlayerInfo_ProcessUnban,
                             cmd_PlayerInfo_ProcessClassChange,
                             cmd_PlayerInfo_ProcessKick;

        internal static bool Init() {

            SQLiteConnectionStringBuilder connectionBuilder = new SQLiteConnectionStringBuilder();
            connectionBuilder.DataSource = DatabaseFile;

            db = new SQLiteConnection( connectionBuilder.ConnectionString );

            if( File.Exists( DatabaseFile ) ) {
                db.Open();
                using( SQLiteCommand cmd = db.CreateCommand() ) {
                    cmd.CommandText = "SELECT [Value] FROM [ServerData] WHERE [KeyGroup]='PlayerDB' AND [Key]='SchemaVersion'";
                    try {
                        using( SQLiteDataReader reader = cmd.ExecuteReader() ) {
                            if( reader.Read() ) {
                                int fileSchemaVersion = Int32.Parse( reader.GetString( 0 ) );
                                if( fileSchemaVersion < SchemaVersion ) {
                                    Logger.Log( "DB: Database schema is out of date.", LogType.Warning );
                                } else if( fileSchemaVersion > SchemaVersion ) {
                                    Logger.Log( "DB: Database schema was made for a newer version of fCraft. Please update.", LogType.FatalError );
                                    return false;
                                } else {
                                    Logger.Log( "DB: Database file loaded normally.", LogType.SystemActivity );
                                }
                            } else {
                                Logger.Log( "DB: Database schema version not found. Database may be corrupt.", LogType.FatalError );
                                return false;
                            }
                        }
                    } catch( SQLiteException ex ) {
                        Logger.Log( "DB: Could not read database version. Database may be corrupt. Error message: " + ex, LogType.FatalError );
                        return false;
                    }
                }
            } else {
                SQLiteConnection.CreateFile( DatabaseFile );
                db.Open();
                Logger.Log( "DB: Database file not found, creating new one.", LogType.Warning );
                DefineSchema();
                // TODO: import old data
            }

            try {
                PrepareQueries();
                return true;

            } catch( SQLiteException ex ) {
                Logger.Log( "DB: Could not prepare database queries: " + ex, LogType.FatalError );
                return false;
            }
        }

        // TODO: clean up, this is unsafe
        public static void QueuePlayerInfoUpdate( PlayerInfo2 info, string field, object value ) {
            ExecuteNonQuery( "UPDATE [Players] SET [" + field + "]=\"" + value.ToString() + "\" WHERE [ID]=" + info.ID );
        }

        internal static void ExecuteNonQuery( string command ) {
            using( SQLiteCommand cmd = db.CreateCommand() ) {
                cmd.CommandText = command;
                cmd.ExecuteNonQuery();
            }
        }

        static void PrepareQueries() {
            cmd_PlayerInfo_ProcessLogin = db.CreateCommand();
            cmd_PlayerInfo_ProcessLogin.CommandText = @"
UPDATE [Players]
SET [LastIP] = @LastIP,
    [LastLoginDate] = @LastLoginDate,
    [LastSeen] = @LastSeen,
    [TimesVisited] = [TimesVisited]+1
WHERE [ID] = @ID;
";
            cmd_PlayerInfo_ProcessLogin.Parameters.Add( new SQLiteParameter( "@LastIP", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogin.Parameters.Add( new SQLiteParameter( "@LastLoginDate", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogin.Parameters.Add( new SQLiteParameter( "@LastSeen", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogin.Parameters.Add( new SQLiteParameter( "@ID", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogin.Prepare();


            cmd_PlayerInfo_ProcessLogout = db.CreateCommand();
            cmd_PlayerInfo_ProcessLogout.CommandText = @"
BEGIN;
UPDATE [Players] SET [LastSeen]=@LastSeen, [TotalTimeOnServer]=[TotalTimeOnServer]+@SessionDuration WHERE ID=@ID;
INSERT INTO [Sessions] VALUES( @ID, @Login, @LastSeen, @IP, @BlocksPlaced, @BlocksDeleted, @BlocksDrawn, @MessagesWritten, @LeaveReason, @GeoIP );
END;
";
            cmd_PlayerInfo_ProcessLogout.Parameters.Add( new SQLiteParameter( "@LastSeen", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogout.Parameters.Add( new SQLiteParameter( "@SessionDuration", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogout.Parameters.Add( new SQLiteParameter( "@ID", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogout.Parameters.Add( new SQLiteParameter( "@IP", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogout.Parameters.Add( new SQLiteParameter( "@BlocksPlaced", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogout.Parameters.Add( new SQLiteParameter( "@BlocksDeleted", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogout.Parameters.Add( new SQLiteParameter( "@BlocksDrawn", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogout.Parameters.Add( new SQLiteParameter( "@MessagesWritten", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogout.Parameters.Add( new SQLiteParameter( "@LeaveReason", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessLogout.Parameters.Add( new SQLiteParameter( "@GeoIP", DbType.String ) );
            cmd_PlayerInfo_ProcessLogout.Prepare();


            cmd_PlayerInfo_ProcessBan = db.CreateCommand();
            cmd_PlayerInfo_ProcessBan.CommandText = @"
INSERT INTO [Bans]
VALUES( TRUE, @Target, @BanPlayer, @BanDate, @BanReason, @BanMethod, 0, 0, '', 0 );
";
            cmd_PlayerInfo_ProcessBan.Parameters.Add( new SQLiteParameter( "@Target", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessBan.Parameters.Add( new SQLiteParameter( "@BanPlayer", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessBan.Parameters.Add( new SQLiteParameter( "@BanDate", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessBan.Parameters.Add( new SQLiteParameter( "@BanReason", DbType.String ) );
            cmd_PlayerInfo_ProcessBan.Parameters.Add( new SQLiteParameter( "@BanMethod", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessBan.Prepare();


            cmd_PlayerInfo_ProcessUnban = db.CreateCommand();
            cmd_PlayerInfo_ProcessUnban.CommandText = @"
UPDATE [Bans] SET [Active]=FALSE,
                  [UnbanPlayer]=@UnbanPlayer,
                  [UnbanDate]=@UnbanDate,
                  [UnbanReason]=@UnbanReason,
                  [UnbanMethod]=@UnbanMethod
WHERE [Player]=@ID AND [Active]=TRUE
";
            cmd_PlayerInfo_ProcessUnban.Parameters.Add( new SQLiteParameter( "@UnbanPlayer", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessUnban.Parameters.Add( new SQLiteParameter( "@UnbanDate", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessUnban.Parameters.Add( new SQLiteParameter( "@UnbanReason", DbType.String ) );
            cmd_PlayerInfo_ProcessUnban.Parameters.Add( new SQLiteParameter( "@UnbanMethod", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessUnban.Parameters.Add( new SQLiteParameter( "@ID", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessUnban.Prepare();


            cmd_PlayerInfo_ProcessClassChange = db.CreateCommand();
            cmd_PlayerInfo_ProcessClassChange.CommandText = @"
BEGIN;
INSERT INTO [ClassChanges] VALUES( @ID, @Changer, @OldRank, @NewRank, @Type, @Date, @Reason );
UPDATE [Players] SET [Class]=@NewRank WHERE [ID]=@ID;
END;
";
            cmd_PlayerInfo_ProcessClassChange.Parameters.Add( new SQLiteParameter( "@ID", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessClassChange.Parameters.Add( new SQLiteParameter( "@Changer", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessClassChange.Parameters.Add( new SQLiteParameter( "@OldRank", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessClassChange.Parameters.Add( new SQLiteParameter( "@NewRank", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessClassChange.Parameters.Add( new SQLiteParameter( "@Type", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessClassChange.Parameters.Add( new SQLiteParameter( "@Date", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessClassChange.Parameters.Add( new SQLiteParameter( "@Reason", DbType.String ) );
            cmd_PlayerInfo_ProcessClassChange.Prepare();


            cmd_PlayerInfo_ProcessKick = db.CreateCommand();
            cmd_PlayerInfo_ProcessKick.CommandText = @"
INSERT INTO [Kicks] VALUES( @Kicker, @ID, @KickDate, @Reason );
";
            cmd_PlayerInfo_ProcessKick.Parameters.Add( new SQLiteParameter( "@Kicker", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessKick.Parameters.Add( new SQLiteParameter( "@ID", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessKick.Parameters.Add( new SQLiteParameter( "@KickDate", DbType.Int32 ) );
            cmd_PlayerInfo_ProcessKick.Parameters.Add( new SQLiteParameter( "@Reason", DbType.String ) );
            cmd_PlayerInfo_ProcessKick.Prepare();
        }

        static void DefineSchema() {
            using( SQLiteCommand cmd = db.CreateCommand() ) {
                cmd.CommandText = @"
BEGIN;

CREATE TABLE [Bans] (
[Active] BOOLEAN  NULL,
[Target] INTEGER  NULL,
[BanPlayer] INTEGER  NULL,
[BanTimestamp] INTEGER  NULL,
[BanReason] VARCHAR(64)  NULL,
[BanMethod] INTEGER  NULL,
[UnbanPlayer] INTEGER  NULL,
[UnbanDate] INTEGER  NULL,
[UnbanReason] VARCHAR(64)  NULL,
[UnbanMethod] INTEGER  NULL
);

CREATE TABLE [IPBans] (
[Active] BOOLEAN  NULL,
[RangeStart] INTEGER  NULL,
[RangeEnd] INTEGER  NULL,
[BanPlayer] INTEGER  NULL,
[BanDate] INTEGER  NULL,
[BanReason] VARCHAR(64)  NULL,
[BanMethod] INTEGER  NULL,
[UnbanPlayer] INTEGER  NULL,
[UnbanDate] INTEGER  NULL,
[UnbanComment] VARCHAR(64)  NULL,
[UnbanMethod] INTEGER  NULL
);

CREATE TABLE [Kicks] (
[Player] INTEGER  NULL,
[Target] INTEGER  NULL,
[Timestamp] INTEGER  NULL,
[Reason] VARCHAR(64)  NULL
);

CREATE TABLE [Log] (
[ID] INTEGER  NOT NULL PRIMARY KEY AUTOINCREMENT,
[Type] INTEGER  NULL,
[Subtype] INTEGER  NULL,
[Source] INTEGER  NULL,
[Timestamp] INTEGER  NULL,
[Message] TEXT  NULL
);

CREATE TABLE [PlayerData] (
[Player] INTEGER  NULL,
[KeyGroup] VARCHAR(32)  NULL,
[Key] VARCHAR(32)  NULL,
[Value] TEXT  NULL
);

CREATE TABLE [Players] (
[ID] INTEGER  PRIMARY KEY AUTOINCREMENT NOT NULL,
[Name] VARCHAR(16)  UNIQUE NULL,
[State] INTEGER  NULL,
[Rank] INTEGER  NULL,
[BlocksPlaced] INTEGER  NULL,
[BlocksDeleted] INTEGER  NULL,
[BlocksDrawn] INTEGER  NULL,
[FirstLogin] INTEGER  NULL,
[LastLogin] INTEGER  NULL,
[LastSeen] INTEGER  NULL,
[TimeTotal] INTEGER  NULL,
[MessagesWritten] INTEGER  NULL
);

CREATE TABLE [ClassChanges] (
[Target] INTEGER  NULL,
[Player] INTEGER  NULL,
[OldRank] INTEGER  NULL,
[NewRank] INTEGER  NULL,
[Type] INTEGER  NULL,
[Date] INTEGER  NULL,
[Reason] VARCHAR(64)  NULL
);

CREATE TABLE [ServerData] (
[KeyGroup] VARCHAR(32)  NULL,
[Key] VARCHAR(32)  NULL,
[Value] TEXT  NULL
);

CREATE TABLE [Sessions] (
[Player] INTEGER  NULL,
[Start] INTEGER  NULL,
[End] INTEGER  NULL,
[IP] INTEGER  NULL,
[BlocksPlaced] INTEGER  NULL,
[BlocksDeleted] INTEGER  NULL,
[BlocksDrawn] INTEGER  NULL,
[MessagesWritten] INTEGER  NULL,
[LeaveReason] INTEGER  NULL,
[GeoIP] VARCHAR(2)  NULL
);

CREATE TABLE [ClassMapping] (
[Index] INTEGER  NOT NULL PRIMARY KEY,
[ClassID] VARCHAR(33)  NULL
);

CREATE INDEX [iBans] ON [Bans] ( [BanPlayer] );

CREATE INDEX [iIPBans] ON [IPBans] ( [BanPlayer] );

CREATE INDEX [iKicks] ON [Kicks] ( [Player] );

CREATE INDEX [iLog] ON [Log](
[ID]  ASC
);

CREATE INDEX [iPlayerData] ON [PlayerData](
[Player]  ASC,
[Key]  ASC
);

CREATE INDEX [iPlayers_ID] ON [Players](
[ID]  ASC
);

CREATE INDEX [iPlayers_Name] ON [Players](
[Name]  ASC
);

CREATE INDEX [iRankChanges] ON [RankChanges] ( [Target] );

CREATE UNIQUE INDEX [iServerData] ON [ServerData](
[Key]  ASC
);

CREATE INDEX [iSessions] ON [Sessions] ( [Player] );

INSERT INTO [ServerData] VALUES ('PlayerDB','SchemaVersion'," + SchemaVersion + @");

COMMIT;
";
                cmd.ExecuteNonQuery();
            }
        }


        #region Utilities
        static readonly DateTime UnixEpoch = new DateTime( 1970, 1, 1 );

        public static int DateTimeToTimestamp( DateTime timestamp ) {
            return (int)(timestamp - UnixEpoch).TotalSeconds;
        }

        public static DateTime TimestampToDateTime( int timestamp ) {
            return UnixEpoch.AddSeconds( timestamp );
        }


        public static int IPAddressToInt32( IPAddress ipAddress ) {
            return BitConverter.ToInt32( ipAddress.GetAddressBytes().Reverse().ToArray(), 0 );
        }

        public static IPAddress Int32ToIPAddress( int ipAddress ) {
            return new IPAddress( BitConverter.GetBytes( ipAddress ).Reverse().ToArray() );
        }

        #endregion


        #region Parametrized Queries

        public static void ProcessLogin( PlayerInfo2 info, Player player ) {
            info.ProcessLogin( player );
            lock( cmd_PlayerInfo_ProcessLogin ) {
                cmd_PlayerInfo_ProcessLogin.Parameters["@LastIP"].Value = DB.IPAddressToInt32( info.LastIP );
                cmd_PlayerInfo_ProcessLogin.Parameters["@LastLoginDate"].Value = DB.DateTimeToTimestamp( info.LastLoginDate );
                cmd_PlayerInfo_ProcessLogin.Parameters["@LastSeen"].Value = DB.DateTimeToTimestamp( info.LastSeen );
                cmd_PlayerInfo_ProcessLogin.Parameters["@ID"].Value = info.ID;
                cmd_PlayerInfo_ProcessLogin.ExecuteNonQuery();
            }
        }

        public static void ProcessLogout( PlayerInfo2 info, LeaveReason reason ) {
            info.ProcessLogout( reason );
            lock( cmd_PlayerInfo_ProcessLogout ) {
                cmd_PlayerInfo_ProcessLogout.Parameters["@LastSeen"].Value = DB.DateTimeToTimestamp( info.LastSeen );
                cmd_PlayerInfo_ProcessLogout.Parameters["@SessionDuration"].Value = (int)info.LastSessionDuration.TotalSeconds;
                cmd_PlayerInfo_ProcessLogout.Parameters["@ID"].Value = info.ID;
                cmd_PlayerInfo_ProcessLogout.Parameters["@IP"].Value = DB.IPAddressToInt32( info.LastIP );
                cmd_PlayerInfo_ProcessLogout.Parameters["@BlocksPlaced"].Value = info.BlocksPlacedLastSession;
                cmd_PlayerInfo_ProcessLogout.Parameters["@BlocksDeleted"].Value = info.BlocksDeletedLastSession;
                cmd_PlayerInfo_ProcessLogout.Parameters["@BlocksDrawn"].Value = info.BlocksDrawnLastSession;
                cmd_PlayerInfo_ProcessLogout.Parameters["@MessagesWritten"].Value = info.MessagesWrittenLastSession;
                cmd_PlayerInfo_ProcessLogout.Parameters["@LeaveReason"].Value = info.LastLeaveReason.ToString();
                cmd_PlayerInfo_ProcessLogout.Parameters["@GeoIP"].Value = ""; //TODO GEOIP
                cmd_PlayerInfo_ProcessLogout.ExecuteNonQuery();
            }
        }

        public static void ProcessBan( PlayerInfo2 info, Player banner, string reason, BanMethod method ) {
            info.ProcessBan( banner, reason, method );
            //TODO: banner.info.ProcessBanOther();
            lock( cmd_PlayerInfo_ProcessBan ) {
                cmd_PlayerInfo_ProcessBan.Parameters["@Target"].Value = info.ID;
                cmd_PlayerInfo_ProcessBan.Parameters["@BanPlayer"].Value = 0;//TODO ID
                cmd_PlayerInfo_ProcessBan.Parameters["@BanDate"].Value = info.BanDate;
                cmd_PlayerInfo_ProcessBan.Parameters["@BanReason"].Value = reason;
                cmd_PlayerInfo_ProcessBan.Parameters["@BanMethod"].Value = (int)method;
                cmd_PlayerInfo_ProcessBan.ExecuteNonQuery();
            }
        }

        public static void ProcessUnban( PlayerInfo2 info, Player unbanner, string reason, UnbanMethod method ) {
            info.ProcessUnban( unbanner, reason, method );
            lock( cmd_PlayerInfo_ProcessUnban ) {
                cmd_PlayerInfo_ProcessUnban.Parameters["@UnbanPlayer"].Value = 0;//TODO ID
                cmd_PlayerInfo_ProcessUnban.Parameters["@UnbanDate"].Value = info.BanDate;
                cmd_PlayerInfo_ProcessUnban.Parameters["@UnbanReason"].Value = reason;
                cmd_PlayerInfo_ProcessUnban.Parameters["@UnbanMethod"].Value = (int)method;
                cmd_PlayerInfo_ProcessUnban.Parameters["@ID"].Value = info.ID;
                cmd_PlayerInfo_ProcessUnban.ExecuteNonQuery();
            }
        }

        public static void ProcessClassChange( PlayerInfo2 info, PlayerClass newClass, Player changer, string reason ) {
            info.ProcessClassChange( newClass, changer, reason );
            lock( cmd_PlayerInfo_ProcessClassChange ) {
                cmd_PlayerInfo_ProcessClassChange.Parameters["@ID"].Value = info.ID;
                cmd_PlayerInfo_ProcessClassChange.Parameters["@Changer"].Value = 0;//TODO ID
                cmd_PlayerInfo_ProcessClassChange.Parameters["@OldRank"].Value = info.PreviousClass;
                cmd_PlayerInfo_ProcessClassChange.Parameters["@NewRank"].Value = info.PlayerClass;
                cmd_PlayerInfo_ProcessClassChange.Parameters["@Type"].Value = (info.PlayerClass.rank - info.PreviousClass.rank);
                cmd_PlayerInfo_ProcessClassChange.Parameters["@Date"].Value = info.ClassChangeDate;
                cmd_PlayerInfo_ProcessClassChange.Parameters["@Reason"].Value = reason;
                cmd_PlayerInfo_ProcessClassChange.ExecuteNonQuery();
            }
        }

        public static void ProcessKick( PlayerInfo2 info, Player kicker, string reason ) {
            info.ProcessKick( kicker, reason );
            //TODO: kicker.info.ProcessKickOther();
            lock( cmd_PlayerInfo_ProcessKick ) {
                cmd_PlayerInfo_ProcessKick.Parameters["@ID"].Value = info.ID;
                cmd_PlayerInfo_ProcessKick.Parameters["@Kicker"].Value = 0;//TODO ID
                cmd_PlayerInfo_ProcessKick.Parameters["@KickDate"].Value = DateTimeToTimestamp( DateTime.Now );
                cmd_PlayerInfo_ProcessKick.Parameters["@Reason"].Value = reason;
                cmd_PlayerInfo_ProcessKick.ExecuteNonQuery();
            }
        }

        #endregion
    }
}