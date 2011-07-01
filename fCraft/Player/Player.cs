﻿// Copyright 2009, 2010, 2011 Matvei Stefarov <me@matvei.org>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Threading;

namespace fCraft {
    /// <summary> Callback for a player-made selection of one or more blocks on a map.
    /// A command may request a number of marks/blocks to select, and a specify callback
    /// to be executed when the desired number of marks/blocks is reached. </summary>
    /// <param name="player"> Player who made the selection. </param>
    /// <param name="marks"> An array of 3D marks/blocks, in terms of block coordinates. </param>
    /// <param name="tag"> An optional argument to pass to the callback,
    /// the value of player.selectionArgs </param>
    public delegate void SelectionCallback( Player player, Position[] marks, object tag );


    /// <summary> Object representing volatile state of connected player.
    /// For persistent state of a known player account, see PlayerInfo. </summary>
    public sealed class Player : IClassy {

        /// <summary> The godly pseudo-player for commands called from the server console.
        /// Console has all the permissions granted.
        /// Note that Player.Console.World is always null,
        /// and that prevents console from calling certain commands (like /tp). </summary>
        public static Player Console, AutoRank;
        bool isSuper;

        public static bool RelayAllUpdates;

        public readonly Session Session;
        public readonly PlayerInfo Info;

        public Position Position,
                        LastValidPosition; // used in speedhack detection

        public bool IsPainting,
                    IsHidden,
                    IsDeaf,
                    IsDisconnected;

        public World World;
        internal DateTime LastActiveTime = DateTime.UtcNow; // used for afk kicks
        internal DateTime LastPatrolTime = DateTime.MinValue;

        // confirmation
        public Command CommandToConfirm { get; private set; }
        public DateTime ConfirmRequestTime { get; private set; }

        /// <summary> Last command called by the player. </summary>
        public Command LastCommand { get; private set; }


        // This constructor is used to create dummy players (such as Console and /dummy)
        // It will soon be replaced by a generic Entity class
        internal Player( string name ) {
            if( name == null ) throw new ArgumentNullException( "name" );
            Info = new PlayerInfo( name, RankManager.HighestRank, true, RankChangeType.AutoPromoted );
            spamBlockLog = new Queue<DateTime>( Info.Rank.AntiGriefBlocks );
            ResetAllBinds();
            isSuper = true;
        }


        // Normal constructor
        internal Player( string name, Session session, Position position ) {
            if( name == null ) throw new ArgumentNullException( "name" );
            if( session == null ) throw new ArgumentNullException( "session" );
            Session = session;
            Position = position;
            Info = PlayerDB.FindOrCreateInfoForPlayer( name, session.IP );
            spamBlockLog = new Queue<DateTime>( Info.Rank.AntiGriefBlocks );
            ResetAllBinds();
        }


        #region Name

        /// <summary> Plain version of the name (no formatting). </summary>
        public string Name {
            get { return Info.Name; }
        }


        /// <summary> Name formatted for display in the player list. </summary>
        public string ListName {
            get {
                string displayedName = Name;
                if( ConfigKey.RankPrefixesInList.Enabled() ) {
                    displayedName = Info.Rank.Prefix + displayedName;
                }
                if( ConfigKey.RankColorsInChat.Enabled() && Info.Rank.Color != Color.White ) {
                    displayedName = Info.Rank.Color + displayedName;
                }
                return displayedName;
            }
        }


        /// <summary> Name formatted for display in chat. </summary>
        public string ClassyName {
            get { return Info.ClassyName; }
        }


        /// <summary> Name formatted for the debugger. </summary>
        public override string ToString() {
            return String.Format( "Player({0})", Info.Name );
        }

        #endregion


        #region Sending

        /// <summary> Send a packet to the player, thread-safe. </summary>
        public void Send( Packet packet ) {
            if( Session != null ) Session.Send( packet );
        }


        /// <summary> Send a packet to the player, thread-safe. </summary>
        public void SendLowPriority( Packet packet ) {
            if( Session != null ) Session.SendLowPriority( packet );
        }

        #endregion


        #region Messaging
        
        const int ConfirmationTimeout = 60;

        int muteWarnings;
        string partialMessage;


        // Parses message incoming from the player
        public void ParseMessage( string rawMessage, bool fromConsole ) {
            if( rawMessage == null ) throw new ArgumentNullException( "rawMessage" );

            if( partialMessage != null ) {
                rawMessage = partialMessage + rawMessage;
                partialMessage = null;
            }

            switch( CommandManager.GetMessageType( rawMessage ) ) {
                case MessageType.Chat: {
                        if( !Can( Permission.Chat ) ) return;

                        if( Info.IsMuted ) {
                            MessageMuted();
                            return;
                        }

                        if( DetectChatSpam() ) return;

                        // Escaped slash removed AFTER logging, to avoid confusion with real commands
                        if( rawMessage.StartsWith( "//" ) ) {
                            rawMessage = rawMessage.Substring( 1 );
                        }

                        if( rawMessage.EndsWith( "//" ) ) {
                            rawMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                        }

                        if( Can( Permission.UseColorCodes ) && rawMessage.Contains( "%" ) ) {
                            rawMessage = Color.ReplacePercentCodes( rawMessage );
                        }

                        Chat.SendGlobal( this, rawMessage );
                    } break;


                case MessageType.Command: {
                        if( rawMessage.EndsWith( "//" ) ) {
                            rawMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                        }
                        Logger.Log( "{0}: {1}", LogType.UserCommand,
                                    Name, rawMessage );
                        Command cmd = new Command( rawMessage );
                        LastCommand = cmd;
                        CommandManager.ParseCommand( this, cmd, fromConsole );
                    } break;


                case MessageType.RepeatCommand: {
                        if( LastCommand == null ) {
                            Message( "No command to repeat." );
                        } else {
                            LastCommand.Rewind();
                            Logger.Log( "{0}: repeat {1}", LogType.UserCommand,
                                        Name, LastCommand.Message );
                            Message( "Repeat: {0}", LastCommand.Message );
                            CommandManager.ParseCommand( this, LastCommand, fromConsole );
                        }
                    } break;


                case MessageType.PrivateChat: {
                        if( !Can( Permission.Chat ) ) return;

                        if( Info.IsMuted ) {
                            MessageMuted();
                            return;
                        }

                        if( DetectChatSpam() ) return;

                        if( rawMessage.EndsWith( "//" ) ) {
                            rawMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                        }

                        string otherPlayerName, messageText;
                        if( rawMessage[1] == ' ' ) {
                            otherPlayerName = rawMessage.Substring( 2, rawMessage.IndexOf( ' ', 2 ) - 2 );
                            messageText = rawMessage.Substring( rawMessage.IndexOf( ' ', 2 ) + 1 );
                        } else {
                            otherPlayerName = rawMessage.Substring( 1, rawMessage.IndexOf( ' ' ) - 1 );
                            messageText = rawMessage.Substring( rawMessage.IndexOf( ' ' ) + 1 );
                        }

                        if( messageText.Contains( "%" ) && Can( Permission.UseColorCodes ) ) {
                            messageText = Color.ReplacePercentCodes( messageText );
                        }

                        // first, find ALL players (visible and hidden)
                        Player[] allPlayers = Server.FindPlayers( otherPlayerName );

                        // if there is more than 1 target player, exclude hidden players
                        if( allPlayers.Length > 1 ) {
                            allPlayers = Server.FindPlayers( this, otherPlayerName );
                        }

                        if( allPlayers.Length == 1 ) {
                            Player target = allPlayers[0];
                            if( target.IsIgnoring( Info ) ) {
                                if( CanSee( target ) ) {
                                    MessageNow( "&WCannot PM {0}&W: you are ignored.", target.ClassyName );
                                }
                            } else {
                                Chat.SendPM( this, target, messageText );
                                if( !CanSee( target ) ) {
                                    // message was sent to a hidden player
                                    MessageNoPlayer( otherPlayerName );

                                } else {
                                    // message was sent normally
                                    Message( "&Pto {0}: {1}",
                                             target.Name, messageText );
                                }
                            }

                        } else if( allPlayers.Length == 0 ) {
                            MessageNoPlayer( otherPlayerName );

                        } else {
                            MessageManyMatches( "player", allPlayers );
                        }
                    } break;


                case MessageType.RankChat: {
                        if( !Can( Permission.Chat ) ) return;

                        if( Info.IsMuted ) {
                            MessageMuted();
                            return;
                        }

                        if( DetectChatSpam() ) return;

                        if( rawMessage.EndsWith( "//" ) ) {
                            rawMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                        }

                        string rankName = rawMessage.Substring( 2, rawMessage.IndexOf( ' ' ) - 2 );
                        Rank rank = RankManager.FindRank( rankName );
                        if( rank != null ) {
                            Logger.Log( "{0} to rank {1}: {2}", LogType.RankChat,
                                        Name, rank.Name, rawMessage );
                            string messageText = rawMessage.Substring( rawMessage.IndexOf( ' ' ) + 1 );
                            if( messageText.Contains( "%" ) && Can( Permission.UseColorCodes ) ) {
                                messageText = Color.ReplacePercentCodes( messageText );
                            }

                            Chat.SendRank( this, rank, messageText );
                        } else {
                            Message( "No rank found matching \"{0}\"", rankName );
                        }
                    } break;


                case MessageType.Confirmation: {
                        if( CommandToConfirm != null ) {
                            if( DateTime.UtcNow.Subtract( ConfirmRequestTime ).TotalSeconds < ConfirmationTimeout ) {
                                CommandToConfirm.IsConfirmed = true;
                                CommandManager.ParseCommand( this, CommandToConfirm, fromConsole );
                                CommandToConfirm = null;
                            } else {
                                MessageNow( "Confirmation timed out. Enter the command again." );
                            }
                        } else {
                            MessageNow( "There is no command to confirm." );
                        }
                    } break;


                case MessageType.PartialMessage:
                    partialMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                    MessageNow( "Partial: &F{0}", partialMessage );
                    break;

                case MessageType.PartialMessageCancel:
                    partialMessage = null;
                    MessageNow( "Partial message cancelled." );
                    break;

                case MessageType.Invalid: {
                        Message( "Unknown command." );
                    } break;
            }
        }


        public void Message( string message ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( this == Console ) {
                Logger.LogToConsole( message );
            } else {
                foreach( Packet p in LineWrapper.Wrap( Color.Sys + message ) ) {
                    Session.Send( p );
                }
            }
        }


        public void Message( string message, params object[] args ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( args == null ) throw new ArgumentNullException( "args" );
            Message( String.Format( message, args ) );
        }


        // Queues a system message with a custom color
        public void MessagePrefixed( string prefix, string message, params object[] args ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( args.Length > 0 ) {
                message = String.Format( message, args );
            }
            if( this == Console ) {
                Logger.LogToConsole( message );
            } else {
                foreach( Packet p in LineWrapper.WrapPrefixed( prefix, Color.Sys + message ) ) {
                    Session.Send( p );
                }
            }
        }


        internal void MessageNow( string message, params object[] args ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( args.Length > 0 ) {
                message = String.Format( message, args );
            }
            if( Session == null ) {
                Logger.LogToConsole( message );
            } else {
                foreach( Packet p in LineWrapper.Wrap( Color.Sys + message ) ) {
                    Session.SendNow( p );
                }
            }
        }


        public void AskForConfirmation( Command cmd, string message, params object[] args ) {
            if( cmd == null ) throw new ArgumentNullException( "cmd" );
            if( message == null ) throw new ArgumentNullException( "message" );
            CommandToConfirm = cmd;
            ConfirmRequestTime = DateTime.UtcNow;
            Message( "{0} Type &H/ok&S to continue.", String.Format( message, args ) );
            CommandToConfirm.Rewind();
        }


        public void MessageNoPlayer( string playerName ) {
            Message( "No players found matching \"{0}\"", playerName );
        }


        public void MessageNoWorld( string worldName ) {
            Message( "No world found with the name \"{0}\"", worldName );
        }


        public void MessageManyMatches( string itemType, IEnumerable<IClassy> names ) {
            if( itemType == null ) throw new ArgumentNullException( "itemType" );
            if( names == null ) throw new ArgumentNullException( "names" );

            string nameList = names.JoinToString( ", ",
                                                  p => p.ClassyName );
            Message( "More than one {0} matched: {1}",
                     itemType, nameList );
        }


        public void MessageNoAccess( params Permission[] permissions ) {
            Rank reqRank = RankManager.GetMinRankWithPermission( permissions );
            if( reqRank == null ) {
                Message( "This command is disabled on the server." );
            } else {
                Message( "This command requires {0}+&S rank.",
                         reqRank.ClassyName );
            }
        }


        public void MessageNoRank( string rankName ) {
            Message( "Unrecognized rank \"{0}\"", rankName );
        }


        public void MessageUnsafePath() {
            Message( "&WYou cannot access files outside the map folder." );
        }


        public void MessageNoZone( string zoneName ) {
            Message( "No zone found with the name \"{0}\". See &H/zones", zoneName );
        }


        public void MessageMuted() {
            Message( "You are muted for another {0:0} seconds.",
                     Info.MutedUntil.Subtract( DateTime.UtcNow ).TotalSeconds );
        }


        #region Ignore

        readonly HashSet<PlayerInfo> ignoreList = new HashSet<PlayerInfo>();
        readonly object ignoreLock = new object();


        /// <summary> Checks whether this player is currently ignoring a given PlayerInfo.</summary>
        public bool IsIgnoring( PlayerInfo other ) {
            lock( ignoreLock ) {
                return ignoreList.Contains( other );
            }
        }


        /// <summary> Adds a given PlayerInfo to the ignore list.
        /// Not that ignores are not persistent, and are reset when a player disconnects. </summary>
        /// <param name="other"> Player to ignore. </param>
        /// <returns> True if the player is now ignored,
        /// false is the player has already been ignored previously. </returns>
        public bool Ignore( PlayerInfo other ) {
            lock( ignoreLock ) {
                if( !ignoreList.Contains( other ) ) {
                    ignoreList.Add( other );
                    return true;
                } else {
                    return false;
                }
            }
        }


        /// <summary> Removes a given PlayerInfo from the ignore list. </summary>
        /// <param name="other"> PlayerInfo to unignore. </param>
        /// <returns> True if the player is no longer ignored,
        /// false if the player was already not ignored. </returns>
        public bool Unignore( PlayerInfo other ) {
            lock( ignoreLock ) {
                return ignoreList.Remove( other );
            }
        }


        /// <summary> Returns a list of all currently-ignored players. </summary>
        public PlayerInfo[] IgnoreList {
            get {
                lock( ignoreLock ) {
                    return ignoreList.ToArray();
                }
            }
        }

        #endregion


        #region AntiSpam

        public static int AntispamMessageCount = 3;
        public static int AntispamInterval = 4;
        readonly Queue<DateTime> spamChatLog = new Queue<DateTime>( AntispamMessageCount );

        bool DetectChatSpam() {
            if( isSuper ) return false;
            if( spamChatLog.Count >= AntispamMessageCount ) {
                DateTime oldestTime = spamChatLog.Dequeue();
                if( DateTime.UtcNow.Subtract( oldestTime ).TotalSeconds < AntispamInterval ) {
                    muteWarnings++;
                    if( muteWarnings > ConfigKey.AntispamMaxWarnings.GetInt() ) {
                        Session.KickNow( "You were kicked for repeated spamming.", LeaveReason.MessageSpamKick );
                        Server.Message( "&W{0} was kicked for repeated spamming.", ClassyName );
                    } else {
                        TimeSpan autoMuteDuration = TimeSpan.FromSeconds( ConfigKey.AntispamMuteDuration.GetInt() );
                        Info.Mute( "(antispam)", autoMuteDuration );
                        Message( "You have been muted for {0} seconds. Slow down.", autoMuteDuration );
                    }
                    return true;
                }
            }
            spamChatLog.Enqueue( DateTime.UtcNow );
            return false;
        }

        #endregion

        #endregion


        #region Placing Blocks

        // for grief/spam detection
        readonly Queue<DateTime> spamBlockLog;

        /// <summary> Last blocktype used by the player.
        /// Make sure to use in conjunction with Player.GetBind() to ensure that bindings are properly applied. </summary>
        public Block LastUsedBlockType { get; private set; }

        /// <summary> Max distance that player may be from a block to reach it (hack detection). </summary>
        const int MaxRange = 6 * 32;


        /// <summary> Handles manually-placed/deleted blocks.
        /// Returns true if player's action should result in a kick. </summary>
        public bool PlaceBlock( short x, short y, short h, bool buildMode, Block type ) {

            LastUsedBlockType = type;

            // check if player is frozen or too far away to legitimately place a block
            if( Info.IsFrozen ||
                Math.Abs( x * 32 - Position.X ) > MaxRange ||
                Math.Abs( y * 32 - Position.Y ) > MaxRange ||
                Math.Abs( h * 32 - Position.H ) > MaxRange ) {
                RevertBlockNow( x, y, h );
                return false;
            }

            if( IsSpectating ) {
                Message( "You cannot build or delete while spectating." );
                RevertBlockNow( x, y, h );
                return false;
            }

            if( World.IsLocked ) {
                RevertBlockNow( x, y, h );
                Message( "This map is currently locked (read-only)." );
                return false;
            }

            if( CheckBlockSpam() ) return true;

            // bindings
            bool requiresUpdate = (type != bindings[(byte)type] || IsPainting);
            if( !buildMode && !IsPainting ) {
                type = Block.Air;
            }
            type = bindings[(byte)type];

            // selection handling
            if( SelectionMarksExpected > 0 ) {
                RevertBlockNow( x, y, h );
                SelectionAddMark( new Position( x, y, h ), true );
                return false;
            }

            CanPlaceResult canPlaceResult;
            if( type == Block.Stair && h > 0 && World.Map.GetBlock( x, y, h - 1 ) == Block.Stair ) {
                // stair stacking
                canPlaceResult = CanPlace( x, y, h - 1, Block.DoubleStair, true );
            } else {
                // normal placement
                canPlaceResult = CanPlace( x, y, h, type, true );
            }

            // if all is well, try placing it
            switch( canPlaceResult ) {
                case CanPlaceResult.Allowed:
                    BlockUpdate blockUpdate;
                    if( type == Block.Stair && h > 0 && World.Map.GetBlock( x, y, h - 1 ) == Block.Stair ) {
                        // handle stair stacking
                        blockUpdate = new BlockUpdate( this, x, y, h - 1, Block.DoubleStair );
                        if( !World.FireChangedBlockEvent( ref blockUpdate ) ) {
                            RevertBlockNow( x, y, h );
                            return false;
                        }
                        Info.ProcessBlockPlaced( (byte)Block.DoubleStair );
                        World.Map.QueueUpdate( blockUpdate );
                        Server.RaisePlayerPlacedBlockEvent( this, x, y, (short)(h - 1), Block.Stair, Block.DoubleStair, true );
                        Session.SendNow( PacketWriter.MakeSetBlock( x, y, h - 1, Block.DoubleStair ) );
                        RevertBlockNow( x, y, h );
                        break;

                    } else {
                        // handle normal blocks
                        blockUpdate = new BlockUpdate( this, x, y, h, type );
                        if( !World.FireChangedBlockEvent( ref blockUpdate ) ) {
                            RevertBlockNow( x, y, h );
                            return false;
                        }
                        Info.ProcessBlockPlaced( (byte)type );
                        Block old = World.Map.GetBlock( x, y, h );
                        World.Map.QueueUpdate( blockUpdate );
                        Server.RaisePlayerPlacedBlockEvent( this, x, y, h, old, type, true );
                        if( requiresUpdate || RelayAllUpdates ) {
                            Session.SendNow( PacketWriter.MakeSetBlock( x, y, h, type ) );
                        }
                    }
                    break;

                case CanPlaceResult.BlocktypeDenied:
                    Message( "&WYou are not permitted to affect this block type." );
                    RevertBlockNow( x, y, h );
                    break;

                case CanPlaceResult.RankDenied:
                    Message( "&WYour rank is not allowed to build." );
                    RevertBlockNow( x, y, h );
                    break;

                case CanPlaceResult.WorldDenied:
                    switch( World.BuildSecurity.CheckDetailed( Info ) ) {
                        case SecurityCheckResult.RankTooLow:
                        case SecurityCheckResult.RankTooHigh:
                            Message( "&WYour rank is not allowed to build in this world." );
                            break;
                        case SecurityCheckResult.BlackListed:
                            Message( "&WYou are not allowed to build in this world." );
                            break;
                    }
                    RevertBlockNow( x, y, h );
                    break;

                case CanPlaceResult.ZoneDenied:
                    Zone deniedZone = World.Map.Zones.FindDenied( x, y, h, this );
                    if( deniedZone != null ) {
                        Message( "&WYou are not allowed to build in zone \"{0}\".", deniedZone.Name );
                    } else {
                        Message( "&WYou are not allowed to build here." );
                    }
                    RevertBlockNow( x, y, h );
                    break;

                case CanPlaceResult.PluginDenied:
                    RevertBlockNow( x, y, h );
                    break;

                //case CanPlaceResult.PluginDeniedNoUpdate:
                //    break;
            }
            return false;
        }


        /// <summary>  Gets the block from given location in player's world,
        /// and sends it (async) to the player.
        /// Used to undo player's attempted block placement/deletion. </summary>
        public void RevertBlock( short x, short y, short h ) {
            Session.SendLowPriority( PacketWriter.MakeSetBlock( x, y, h, World.Map.GetBlockByte( x, y, h ) ) );
        }


        /// <summary>  Gets the block from given location in player's world, and sends it (sync) to the player.
        /// Used to undo player's attempted block placement/deletion.
        /// To avoid threading issues, only use this from this player's IoThread. </summary>
        internal void RevertBlockNow( short x, short y, short h ) {
            Session.SendNow( PacketWriter.MakeSetBlock( x, y, h, World.Map.GetBlockByte( x, y, h ) ) );
        }


        bool CheckBlockSpam() {
            if( Info.Rank.AntiGriefBlocks == 0 || Info.Rank.AntiGriefSeconds == 0 ) return false;
            if( spamBlockLog.Count >= Info.Rank.AntiGriefBlocks ) {
                DateTime oldestTime = spamBlockLog.Dequeue();
                double spamTimer = DateTime.UtcNow.Subtract( oldestTime ).TotalSeconds;
                if( spamTimer < Info.Rank.AntiGriefSeconds ) {
                    Session.KickNow( "You were kicked by antigrief system. Slow down.", LeaveReason.BlockSpamKick );
                    Server.Message( "{0}&W was kicked for suspected griefing.", ClassyName );
                    Logger.Log( "{0} was kicked for block spam ({1} blocks in {2} seconds)", LogType.SuspiciousActivity,
                                Name, Info.Rank.AntiGriefBlocks, spamTimer );
                    return true;
                }
            }
            spamBlockLog.Enqueue( DateTime.UtcNow );
            return false;
        }

        #endregion


        #region Binding

        readonly Block[] bindings = new Block[50];

        public void Bind( Block type, Block replacement ) {
            bindings[(byte)type] = replacement;
        }

        public void ResetBind( Block type ) {
            bindings[(byte)type] = type;
        }

        public void ResetBind( params Block[] types ) {
            foreach( Block type in types ) {
                ResetBind( type );
            }
        }

        public Block GetBind( Block type ) {
            return bindings[(byte)type];
        }

        public void ResetAllBinds() {
            foreach( Block block in Enum.GetValues( typeof( Block ) ) ) {
                if( block != Block.Undefined ) {
                    ResetBind( block );
                }
            }
        }

        #endregion


        #region Permission Checks

        public bool Can( params Permission[] permissions ) {
            return isSuper || permissions.All( permission => Info.Rank.Can( permission ) );
        }

        public bool CanAny( params Permission[] permissions ) {
            return isSuper || permissions.Any( permission => Info.Rank.Can( permission ) );
        }

        public bool Can( Permission permission ) {
            return isSuper || Info.Rank.Can( permission );
        }


        public bool Can( Permission permission, Rank other ) {
            return isSuper || Info.Rank.Can( permission, other );
        }


        public bool CanDraw( int volume ) {
            return isSuper || (Info.Rank.DrawLimit == 0) || (volume <= Info.Rank.DrawLimit);
        }


        public bool CanJoin( World worldToJoin ) {
            if( worldToJoin == null ) throw new ArgumentNullException( "worldToJoin" );
            return isSuper || worldToJoin.AccessSecurity.Check( Info );
        }


        public CanPlaceResult CanPlace( int x, int y, int h, Block newBlock, bool isManual ) {
            CanPlaceResult result;

            // check deleting admincrete
            Block block = World.Map.GetBlock( x, y, h );

            // check special blocktypes
            if( newBlock == Block.Admincrete && !Can( Permission.PlaceAdmincrete ) ) {
                result = CanPlaceResult.BlocktypeDenied;
                goto eventCheck;
            } else if( (newBlock == Block.Water || newBlock == Block.StillWater) && !Can( Permission.PlaceWater ) ) {
                result = CanPlaceResult.BlocktypeDenied;
                goto eventCheck;
            } else if( (newBlock == Block.Lava || newBlock == Block.StillLava) && !Can( Permission.PlaceLava ) ) {
                result = CanPlaceResult.BlocktypeDenied;
                goto eventCheck;
            }

            if( block == Block.Admincrete && !Can( Permission.DeleteAdmincrete ) ) {
                result = CanPlaceResult.BlocktypeDenied;
                goto eventCheck;
            }

            // check zones & world permissions
            PermissionOverride zoneCheckResult = World.Map.Zones.Check( x, y, h, this );
            if( zoneCheckResult == PermissionOverride.Allow ) {
                result = CanPlaceResult.Allowed;
                goto eventCheck;
            } else if( zoneCheckResult == PermissionOverride.Deny ) {
                result = CanPlaceResult.ZoneDenied;
                goto eventCheck;
            }

            // Check world permissions
            switch( World.BuildSecurity.CheckDetailed( Info ) ) {
                case SecurityCheckResult.Allowed:
                    // Check rank permissions
                    if( (Can( Permission.Build ) || newBlock == Block.Air) &&
                        (Can( Permission.Delete ) || block == Block.Air) ) {
                        result = CanPlaceResult.Allowed;
                        goto eventCheck;
                    } else {
                        result = CanPlaceResult.RankDenied;
                        goto eventCheck;
                    }
                case SecurityCheckResult.WhiteListed:
                    result = CanPlaceResult.Allowed;
                    goto eventCheck;
                default:
                    result = CanPlaceResult.WorldDenied;
                    goto eventCheck;
            }

        eventCheck:
            return Server.RaisePlayerPlacingBlockEvent( this, (short)x, (short)y, (short)h, block, newBlock, isManual, result );
        }


        // Determines what OP-code to send to the player. It only matters for deleting admincrete.
        public byte GetOpPacketCode() {
            return (byte)(Can( Permission.DeleteAdmincrete ) ? 100 : 0);
        }


        public bool CanSee( Player other ) {
            if( other == null ) throw new ArgumentNullException( "other" );
            if( isSuper ) return true;
            return !other.IsHidden || Info.Rank.CanSee( other.Info.Rank );
        }

        #endregion


        #region Drawing, Selection, and Undo

        internal Queue<BlockUpdate> UndoBuffer = new Queue<BlockUpdate>();


        public SelectionCallback SelectionCallback { get; private set; }
        public readonly Queue<Position> SelectionMarks = new Queue<Position>();
        public int SelectionMarkCount,
                   SelectionMarksExpected;
        internal object SelectionArgs { get; private set; } // can be used for 'block' or 'zone' or whatever
        internal Permission[] SelectionPermissions;

        internal BuildingCommands.CopyInformation CopyInformation;

        public void SelectionAddMark( Position pos, bool executeCallbackIfNeeded ) {
            SelectionMarks.Enqueue( pos );
            SelectionMarkCount++;
            if( SelectionMarkCount >= SelectionMarksExpected ) {
                if( executeCallbackIfNeeded ) {
                    SelectionExecute();
                } else {
                    Message( "Last block marked at ({0},{1},{2}). Type &H/mark&S or click any block to continue.",
                             pos.X, pos.Y, pos.H );
                }
            } else {
                Message( "Block #{0} marked at ({1},{2},{3}). Place mark #{4}.",
                         SelectionMarkCount, pos.X, pos.Y, pos.H, SelectionMarkCount + 1 );
            }
        }

        public void SelectionExecute() {
            SelectionMarksExpected = 0;
            if( SelectionPermissions == null || Can( SelectionPermissions ) ) {
                SelectionCallback( this, SelectionMarks.ToArray(), SelectionArgs );
            } else {
                Message( "&WYou are no longer allowed to complete this action." );
                MessageNoAccess( SelectionPermissions );
            }
        }

        public void SelectionSetCallback( int marksExpected, SelectionCallback callback, object args, params Permission[] requiredPermissions ) {
            SelectionArgs = args;
            SelectionMarksExpected = marksExpected;
            SelectionMarks.Clear();
            SelectionMarkCount = 0;
            SelectionCallback = callback;
            SelectionPermissions = requiredPermissions;
        }

        public void SelectionResetMarks() {
            SelectionMarks.Clear();
            SelectionMarkCount = 0;
        }

        public void SelectionCancel() {
            SelectionMarksExpected = 0;
        }

        public bool IsMakingSelection {
            get { return SelectionMarksExpected > 0; }
        }

        #endregion


        #region Spectating

        public bool IsSpectating {
            get {
                return (Session != null) && (Session.SpectatedPlayer != null);
            }
        }


        public bool Spectate( Player target ) {
            if( target == null ) throw new ArgumentNullException( "target" );
            if( target == this ) throw new ArgumentException( "Cannot spectate self.", "target" );
            Message( "Now spectating {0}&S. Type &H/unspec&S to stop.", target.ClassyName );
            return (Interlocked.Exchange( ref Session.SpectatedPlayer, target ) == null);
        }


        public bool StopSpectating() {
            Player wasSpectating = Interlocked.Exchange( ref Session.SpectatedPlayer, null );
            if( wasSpectating != null ) {
                Message( "Stopped spectating {0}", wasSpectating.ClassyName );
                return true;
            } else {
                return false;
            }
        }

        #endregion


        #region Static Utilities

        const string PaidCheckUrl = "http://www.minecraft.net/haspaid.jsp?user=";
        const int PaidCheckTimeout = 5000;

        // binding delegate for checking the status
        static IPEndPoint BindIPEndPointCallback( ServicePoint servicePoint, IPEndPoint remoteEndPoint, int retryCount ) {
            return new IPEndPoint( Server.IP, 0 );
        }


        /// <summary> Checks whether a given player has a paid minecraft.net account. </summary>
        /// <returns> True if the account is paid. False if it is not paid, or if information is unavailable. </returns>
        public static bool CheckPaidStatus( string name ) {
            if( name == null ) throw new ArgumentNullException( "name" );
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create( PaidCheckUrl + Uri.EscapeDataString( name ) );
            request.ServicePoint.BindIPEndPointDelegate = new BindIPEndPoint( BindIPEndPointCallback );
            request.Timeout = PaidCheckTimeout;
            request.CachePolicy = new RequestCachePolicy( RequestCacheLevel.NoCacheNoStore );

            try {
                using( WebResponse response = request.GetResponse() ) {
                    using( StreamReader responseReader = new StreamReader( response.GetResponseStream() ) ) {
                        string paidStatusString = responseReader.ReadToEnd();
                        bool isPaid;
                        return Boolean.TryParse( paidStatusString, out isPaid ) && isPaid;
                    }
                }
            } catch( WebException ex ) {
                Logger.Log( "Could not check paid status of player {0}: {1}", LogType.Warning,
                            name, ex.Message );
                return false;
            }
        }


        /// <summary> Ensures that a player name has the correct length and character set. </summary>
        public static bool IsValidName( string name ) {
            if( name == null ) throw new ArgumentNullException( "name" );
            if( name.Length < 2 || name.Length > 16 ) return false;
            for( int i = 0; i < name.Length; i++ ) {
                char ch = name[i];
                if( (ch < '0' && ch != '.') || (ch > '9' && ch < 'A') || (ch > 'Z' && ch < '_') || (ch > '_' && ch < 'a') || ch > 'z' ) {
                    return false;
                }
            }
            return true;
        }

        #endregion


        public void ResetIdleTimer() {
            LastActiveTime = DateTime.UtcNow;
        }
    }
}