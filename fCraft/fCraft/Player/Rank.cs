﻿// Copyright 2009, 2010 Matvei Stefarov <me@matvei.org>
using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using System.Xml.Linq;


namespace fCraft {
    public sealed class Rank {

        public static bool operator >( Rank a, Rank b ) {
            return a.Index > b.Index;
        }

        public static bool operator <( Rank a, Rank b ) {
            return a.Index < b.Index;
        }

        public static bool operator >=( Rank a, Rank b ) {
            return a.Index >= b.Index;
        }

        public static bool operator <=( Rank a, Rank b ) {
            return a.Index <= b.Index;
        }


        public sealed class RankDefinitionException : Exception {
            public RankDefinitionException( string message ) : base( message ) { }
            public RankDefinitionException( string message, params string[] args ) :
                base( String.Format( message, args ) ) { }
        }


        public string Name { get; set; }

        public byte legacyNumericRank;

        public string Color { get; set; }

        public string ID { get; set; }

        public bool[] Permissions {
            get;
            private set;
        }



        public string Prefix = "";
        public int IdleKickTimer,
                   DrawLimit,
                   AntiGriefBlocks = 35,
                   AntiGriefSeconds = 5;
        public bool ReservedSlot;
        public int Index;

        public Rank NextRankUp, NextRankDown;


        public Rank() {
            Permissions = new bool[Enum.GetValues( typeof( Permission ) ).Length];
            PermissionLimits = new Rank[Permissions.Length];
            PermissionLimitStrings = new string[Permissions.Length];
        }

        public Rank( XElement el )
            : this() {

            // Name
            XAttribute attr = el.Attribute( "name" );
            if( attr == null ) {
                throw new RankDefinitionException( "Class definition with no name was ignored." );
            }
            if( !Rank.IsValidRankName( attr.Value.Trim() ) ) {
                throw new RankDefinitionException( "Invalid name specified for class \"{0}\". Class names can only contain letters, digits, and underscores. Class definition was ignored.", Name );
            }
            Name = attr.Value.Trim();

            if( RankList.RanksByName.ContainsKey( Name.ToLower() ) ) {
                throw new RankDefinitionException( "Duplicate name for class \"{0}\". Class definition was ignored.", Name );
            }


            // ID
            attr = el.Attribute( "id" );
            if( attr == null ) {
                Logger.Log( "PlayerClass({0}): Issued a new unique ID.", LogType.Warning, Name );
                ID = RankList.GenerateID();

            } else if( !Rank.IsValidID( attr.Value.Trim() ) ) {
                throw new RankDefinitionException( "Invalid ID specified for class \"{0}\". ID must be alphanumeric, and exactly 16 characters long. Class definition was ignored.", Name );

            } else {
                ID = attr.Value.Trim();
                if( RankList.RanksByID.ContainsKey( Name ) ) {
                    throw new RankDefinitionException( "Duplicate ID for {0}. Class definition was ignored.", Name );
                }
            }


            // Rank
            if( (attr = el.Attribute( "rank" )) == null ) {
                throw new RankDefinitionException( "No rank specified for {0}. Class definition was ignored.", Name );
            }
            if( !Byte.TryParse( attr.Value, out legacyNumericRank ) ) {
                throw new RankDefinitionException( "Cannot parse rank for {0}. Class definition was ignored.", Name );
            }


            // Color (optional)
            if( (attr = el.Attribute( "color" )) != null ) {
                if( (Color = fCraft.Color.Parse( attr.Value )) == null ) {
                    Logger.Log( "PlayerClass({0}): Could not parse class color. Assuming default (none).", LogType.Warning, Name );
                }
            } else {
                Color = fCraft.Color.Parse( attr.Value );
            }


            // Prefix (optional)
            if( (attr = el.Attribute( "prefix" )) != null ) {
                if( Rank.IsValidPrefix( attr.Value ) ) {
                    Prefix = attr.Value;
                } else {
                    Logger.Log( "PlayerClass({0}): Invalid prefix format. Expecting 1 character.", LogType.Warning, Name );
                }
            }


            // AntiGrief block limit (assuming unlimited if not given)
            int value = 0;
            if( (el.Attribute( "antiGriefBlocks" ) != null) && (el.Attribute( "antiGriefSeconds" ) != null) ) {
                attr = el.Attribute( "antiGriefBlocks" );
                if( Int32.TryParse( attr.Value, out value ) ) {
                    if( value >= 0 && value < 1000 ) {

                        attr = el.Attribute( "antiGriefSeconds" );
                        if( Int32.TryParse( attr.Value, out value ) ) {
                            if( value >= 0 && value < 100 ) {
                                AntiGriefSeconds = value;
                                AntiGriefBlocks = value;
                            } else {
                                Logger.Log( "PlayerClass({0}): Values for antiGriefSeconds in not within valid range (0-1000). Assuming default ({1}).", LogType.Warning,
                                            Name, AntiGriefSeconds );
                            }
                        } else {
                            Logger.Log( "PlayerClass({0}): Could not parse the value for antiGriefSeconds. Assuming default ({1}).", LogType.Warning,
                                        Name, AntiGriefSeconds );
                        }

                    } else {
                        Logger.Log( "PlayerClass({0}): Values for antiGriefBlocks in not within valid range (0-1000). Assuming default ({1}).", LogType.Warning,
                                    Name, AntiGriefBlocks );
                    }
                } else {
                    Logger.Log( "PlayerClass({0}): Could not parse the value for antiGriefBlocks. Assuming default ({1}).", LogType.Warning,
                                Name, AntiGriefBlocks );
                }
            }


            if( (attr = el.Attribute( "drawLimit" )) != null ) {
                if( Int32.TryParse( attr.Value, out value ) ) {
                    if( value >= 0 && value < 100000000 ) {
                        DrawLimit = value;
                    } else {
                        Logger.Log( "PlayerClass({0}): Values for drawLimit in not within valid range (0-1000). Assuming default ({1}).", LogType.Warning,
                                    Name, DrawLimit );
                    }
                } else {
                    Logger.Log( "PlayerClass({0}): Could not parse the value for drawLimit. Assuming default ({1}).", LogType.Warning,
                                Name, DrawLimit );
                }
            }



            if( (attr = el.Attribute( "idleKickAfter" )) != null ) {
                if( !Int32.TryParse( attr.Value, out IdleKickTimer ) ) {
                    Logger.Log( "PlayerClass({0}): Could not parse the value for idleKickAfter. Assuming 0 (never).", LogType.Warning, Name );
                    IdleKickTimer = 0;
                }
            } else {
                IdleKickTimer = 0;
            }

            if( (attr = el.Attribute( "reserveSlot" )) != null ) {
                if( !Boolean.TryParse( attr.Value, out ReservedSlot ) ) {
                    Logger.Log( "PlayerClass({0}): Could not parse the value for reserveSlot. Assuming \"false\".", LogType.Warning, Name );
                    ReservedSlot = false;
                }
            } else {
                ReservedSlot = false;
            }


            // read permissions
            XElement temp;
            for( int i = 0; i < Enum.GetValues( typeof( Permission ) ).Length; i++ ) {
                string permission = ((Permission)i).ToString();
                if( (temp = el.Element( permission )) != null ) {
                    Permissions[i] = true;
                    if ((attr = temp.Attribute( "max" )) != null){
                        PermissionLimitStrings[i] = attr.Value;
                    }
                }
            }

            // check consistency of ban permissions
            if( !Can( Permission.Ban ) && (Can( Permission.BanAll ) || Can( Permission.BanIP )) ) {
                Logger.Log( "PlayerClass({0}): Class is allowed to BanIP and/or BanAll but not allowed to Ban. " +
                            "Assuming that all ban permissions were ment to be off.", LogType.Warning, Name );
                Permissions[(int)Permission.BanIP] = false;
                Permissions[(int)Permission.BanAll] = false;
            }

            // check consistency of pantrol permissions
            if( !Can( Permission.Teleport ) && Can( Permission.Patrol ) ) {
                Logger.Log( "PlayerClass({0}): Class is allowed to Patrol but not allowed to Teleport. " +
                            "Assuming that Patrol permission was ment to be off.", LogType.Warning, Name );
                Permissions[(int)Permission.Patrol] = false;
            }
        }


        public XElement Serialize() {
            XElement classTag = new XElement( "Rank" );
            classTag.Add( new XAttribute( "name", Name ) );
            classTag.Add( new XAttribute( "id", ID ) );
            classTag.Add( new XAttribute( "rank", legacyNumericRank ) );
            classTag.Add( new XAttribute( "color", fCraft.Color.GetName( Color ) ) );
            if( Prefix.Length > 0 ) classTag.Add( new XAttribute( "prefix", Prefix ) );
            classTag.Add( new XAttribute( "antiGriefBlocks", AntiGriefBlocks ) );
            classTag.Add( new XAttribute( "antiGriefSeconds", AntiGriefSeconds ) );
            if( DrawLimit > 0 ) classTag.Add( new XAttribute( "drawLimit", DrawLimit ) );
            if( IdleKickTimer > 0 ) classTag.Add( new XAttribute( "idleKickAfter", IdleKickTimer ) );
            if( ReservedSlot ) classTag.Add( new XAttribute( "reserveSlot", ReservedSlot ) );

            XElement temp;
            for( int i = 0; i < Enum.GetValues( typeof( Permission ) ).Length; i++ ) {
                if( Permissions[i] ) {
                    temp = new XElement( ((Permission)i).ToString() );

                    if( PermissionLimits[i] != null ) {
                        temp.Add( new XAttribute( "max", GetLimit((Permission)i) ) );
                    }
                    classTag.Add( temp );
                }
            }
            return classTag;
        }

        
        #region Permissions
        public bool Can( Permission permission ) {
            return Permissions[(int)permission];
        }


        public bool CanKick( Rank other ) {
            return GetLimit(Permission.Kick) >= other;
        }

        public bool CanBan( Rank other ) {
            return GetLimit( Permission.Ban ) >= other;
        }

        public bool CanPromote( Rank other ) {
            return GetLimit( Permission.Promote ) >= other;
        }

        public bool CanDemote( Rank other ) {
            return GetLimit( Permission.Demote ) >= other;
        }

        public bool CanSee( Rank other ) {
            return this > other.GetLimit( Permission.Hide );
        }

        #endregion

        #region Permission Limits

        public Rank[] PermissionLimits {
            get;
            private set;
        }
        public string[] PermissionLimitStrings;

        public Rank GetLimit( Permission permission ) {
            if( PermissionLimits[(int)permission] == null ) {
                return this;
            } else {
                return PermissionLimits[(int)permission];
            }
        }

        public void SetLimit( Permission permission, Rank limit ) {
            PermissionLimits[(int)permission] = limit;
        }

        public void ResetLimit( Permission permission ) {
            SetLimit( permission, null );
        }

        public bool IsLimitDefault( Permission permission ) {
            return (PermissionLimits[(int)permission] == null);
        }

        public int GetLimitIndex( Permission permission ) {
            if( PermissionLimits[(int)permission] == null ) {
                return 0;
            } else {
                return PermissionLimits[(int)permission].Index + 1;
            }
        }

        #endregion

        #region Validation

        public static bool IsValidRankName( string rankName ) {
            if( rankName.Length < 1 || rankName.Length > 16 ) return false;
            for( int i = 0; i < rankName.Length; i++ ) {
                char ch = rankName[i];
                if( ch < '0' || (ch > '9' && ch < 'A') || (ch > 'Z' && ch < '_') || (ch > '_' && ch < 'a') || ch > 'z' ) {
                    return false;
                }
            }
            return true;
        }

        public static bool IsValidID( string ID ) {
            if( ID.Length != 16 ) return false;
            for( int i = 0; i < ID.Length; i++ ) {
                char ch = ID[i];
                if( ch < '0' || (ch > '9' && ch < 'A') || (ch > 'Z' && ch < 'a') || ch > 'z' ) {
                    return false;
                }
            }
            return true;
        }

        public static bool IsValidPrefix( string val ) {
            if( val.Length == 0 ) return true;
            if( val.Length > 1 ) return false;
            return val[0] > ' ' && val[0] != '&' && val[0] != '`' && val[0] != '^' && val[0] <= '}';
        }

        #endregion


        public string ToComboBoxOption() {
            return String.Format( "{0,3} {1,1}{2}", legacyNumericRank, Prefix, Name );
        }

        public override string ToString() {
            return Name + "#" + ID;
        }

        public string GetClassyName() {
            string displayedName = Name;
            if( Config.GetBool( ConfigKey.RankPrefixesInChat ) ) {
                displayedName = Prefix + displayedName;
            }
            if( Config.GetBool( ConfigKey.RankColorsInChat ) ) {
                displayedName = Color + displayedName;
            }
            return displayedName;
        }

        internal bool ParsePermissionLimits() {
            bool ok = true;
            for( int i = 0; i < PermissionLimits.Length; i++ ) {
                if( PermissionLimitStrings[i] != null ) {
                    SetLimit( (Permission)i, RankList.ParseRank( PermissionLimitStrings[i] ) );
                    ok &= (GetLimit((Permission)i) != null);
                }
            }
            return ok;
        }
    }
}