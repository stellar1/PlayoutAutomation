﻿
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using System.Threading;
using MySql.Data.MySqlClient;
using System.Diagnostics;
using System.Configuration;
using System.IO;
using System.Xml.Serialization;
using System.Xml;
using System.Collections;
using TAS.Common;
using TAS.Server;

namespace TAS.Data
{
    public static class DatabaseConnector
    {
        private enum TEventFlags : uint { Enabled = 1, Hold = 2 };
        private static MySqlConnection connection;
        private static Timer IdleTimeTimer;
        static bool Connect()
        {
            bool _connectionResult = connection.State == ConnectionState.Open;
            if (!_connectionResult)
            {
                connection.Open();
                _connectionResult = connection.State == ConnectionState.Open;
            }
            Debug.WriteLineIf(!_connectionResult, connection.State, "Not connected"); 
            return _connectionResult;
        }

        public static void Initialize()
        {
            connection = new MySqlConnection(ConfigurationManager.ConnectionStrings["tasConnectionString"].ConnectionString);
            IdleTimeTimer = new Timer(_idleTimeTimerCallback, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            Debug.WriteLine(connection, "Created");
        }

        private static void _idleTimeTimerCallback(object o)
        {
            lock (connection)
                if (!connection.Ping())
                {
                    connection.Close();
                    Connect();
                }
        }

        private static DateTime _readDateTimeField(MySqlDataReader dataReader, string fieldName)
        {
            DateTime result = default(DateTime);
            try
            {
                result = dataReader.IsDBNull(dataReader.GetOrdinal(fieldName)) ? default(DateTime) : DateTime.SpecifyKind(dataReader.GetDateTime(fieldName), DateTimeKind.Utc);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message, e.StackTrace);
            }
            return result;
        }

        internal static void DbReadRootEvents(this Engine engine)
        {
            if (Connect())
            {
                MySqlCommand cmd = new MySqlCommand("SELECT * FROM RundownEvent where typStart in (@StartTypeManual, @StartTypeOnFixedTime, @StartTypeNone) and idEventBinding=0 and idEngine=@idEngine order by ScheduledTime, EventName", connection);
                cmd.Parameters.AddWithValue("@idEngine", engine.IdEngine);
                cmd.Parameters.AddWithValue("@StartTypeManual", (byte)TStartType.Manual);
                cmd.Parameters.AddWithValue("@StartTypeOnFixedTime", (byte) TStartType.OnFixedTime);
                cmd.Parameters.AddWithValue("@StartTypeNone", (byte)TStartType.None);
                Event NewEvent;
                lock (connection)
                {
                    using (MySqlDataReader dataReader = cmd.ExecuteReader())
                    {
                        while (dataReader.Read())
                        {
                            NewEvent = _EventRead(engine, dataReader);
                            engine.RootEvents.Add(NewEvent);
                        }
                        dataReader.Close();
                    }
                }
                Debug.WriteLine(engine, "EventReadRootEvents read");
            }
        }

        internal static void DbSearchMissing(this Engine engine)
        {
            if (Connect())
            {
                MySqlCommand cmd = new MySqlCommand("SELECT * FROM tas.rundownevent m WHERE m.idEngine=@idEngine and (SELECT s.idRundownEvent FROM tas.rundownevent s WHERE m.idEventBinding = s.idRundownEvent) IS NULL", connection);
                cmd.Parameters.AddWithValue("@idEngine", engine.IdEngine);
                Event newEvent;
                List<Event> foundEvents = new List<Event>();
                lock (connection)
                {
                    using (MySqlDataReader dataReader = cmd.ExecuteReader())
                    {
                        while (dataReader.Read())
                        {
                            lock (engine.RootEvents.SyncRoot)
                                if (!engine.RootEvents.Any(e => e._idRundownEvent == dataReader.GetUInt64("idRundownEvent")))
                                {
                                    newEvent = _EventRead(engine, dataReader);
                                    foundEvents.Add(newEvent);
                                }
                        }
                        dataReader.Close();
                    }
                }
                foreach (Event e in foundEvents)
                {
                        e.StartType = TStartType.Manual;
                        e.Save();
                        engine.RootEvents.Add(e);
                }
            }
        }

        internal static SynchronizedCollection<Event> DbReadSubEvents(this Event eventOwner)
        {
            if (Connect())
            {
                var EventList = new SynchronizedCollection<Event>();
                MySqlCommand cmd;
                if (eventOwner != null)
                {
                    cmd = new MySqlCommand("SELECT * FROM RundownEvent where idEventBinding = @idEventBinding and typStart=@StartType;", connection);
                    cmd.Parameters.AddWithValue("@idEventBinding", eventOwner.IdRundownEvent);
                    if (eventOwner.EventType == TEventType.Container)
                        cmd.Parameters.AddWithValue("@StartType", TStartType.Manual);
                    else
                        cmd.Parameters.AddWithValue("@StartType", TStartType.With);
                    Event NewEvent;
                    lock (connection)
                    {
                        using (MySqlDataReader dataReader = cmd.ExecuteReader())
                        {
                            try
                            {
                                while (dataReader.Read())
                                {
                                    NewEvent = _EventRead(eventOwner.Engine, dataReader);
                                    NewEvent._parent = eventOwner;
                                    EventList.Add(NewEvent);
                                }
                            }
                            finally
                            {
                                dataReader.Close();
                            }
                        }
                    }
                }
                return EventList;
            } 
            else 
                return null;
        }

        internal static Event DbReadNext(this Event aEvent)
        {
            if (Connect() && (aEvent != null))
            {
                MySqlCommand cmd = new MySqlCommand("SELECT * FROM RundownEvent where idEventBinding = @idEventBinding and typStart=@StartType;", connection);
                cmd.Parameters.AddWithValue("@idEventBinding", aEvent.IdRundownEvent);
                cmd.Parameters.AddWithValue("@StartType", TStartType.After);
                lock (connection)
                {
                    MySqlDataReader reader = cmd.ExecuteReader();
                    try
                    {
                        if (reader.Read())
                            return _EventRead(aEvent.Engine, reader);
                    }
                    finally
                    {
                        reader.Close();
                    }
                }
            }
            return null;
        }


        private static Event _EventRead(Engine engine, MySqlDataReader dataReader)
        {
            Event aEvent = new Event(engine);
            uint flags = dataReader.IsDBNull(dataReader.GetOrdinal("flagsEvent")) ? 0 : dataReader.GetUInt32("flagsEvent");
            aEvent._idRundownEvent = dataReader.GetUInt64("idRundownEvent");
            aEvent._layer = (VideoLayer)dataReader.GetSByte("Layer");
            aEvent._eventType = (TEventType)dataReader.GetByte("typEvent");
            aEvent._startType = (TStartType)dataReader.GetByte("typStart");
            aEvent._scheduledTime = _readDateTimeField(dataReader, "ScheduledTime");
            aEvent._duration = dataReader.IsDBNull(dataReader.GetOrdinal("Duration")) ? default(TimeSpan) : aEvent.Engine.AlignTimeSpan(dataReader.GetTimeSpan("Duration"));
            aEvent._scheduledDelay = dataReader.IsDBNull(dataReader.GetOrdinal("ScheduledDelay")) ? default(TimeSpan) : aEvent.Engine.AlignTimeSpan(dataReader.GetTimeSpan("ScheduledDelay"));
            aEvent._scheduledTC = dataReader.IsDBNull(dataReader.GetOrdinal("ScheduledTC")) ? TimeSpan.Zero : dataReader.GetTimeSpan("ScheduledTC");
            aEvent._mediaGuid = (dataReader.IsDBNull(dataReader.GetOrdinal("MediaGuid"))) ? Guid.Empty : dataReader.GetGuid("MediaGuid");
            aEvent._eventName = dataReader.IsDBNull(dataReader.GetOrdinal("EventName")) ? default(string) : dataReader.GetString("EventName");
            var psb = dataReader.GetByte("PlayState");
            aEvent._playState = (TPlayState)psb;
            if (aEvent._playState == TPlayState.Playing || aEvent._playState == TPlayState.Paused)
                aEvent._playState = TPlayState.Aborted;
            if (aEvent._playState == TPlayState.Fading)
                aEvent._playState = TPlayState.Played;
            aEvent._startTime = _readDateTimeField(dataReader, "StartTime");
            aEvent._startTC = dataReader.IsDBNull(dataReader.GetOrdinal("StartTC")) ? TimeSpan.Zero : dataReader.GetTimeSpan("StartTC");
            aEvent._requestedStartTime = dataReader.IsDBNull(dataReader.GetOrdinal("RequestedStartTime")) ? null : (TimeSpan?)dataReader.GetTimeSpan("RequestedStartTime");
            aEvent._transitionTime = dataReader.IsDBNull(dataReader.GetOrdinal("TransitionTime")) ? default(TimeSpan) : dataReader.GetTimeSpan("TransitionTime");
            aEvent._transitionType = (TTransitionType)dataReader.GetByte("typTransition");
            aEvent._audioVolume = dataReader.IsDBNull(dataReader.GetOrdinal("AudioVolume")) ? 0 : dataReader.GetDecimal("AudioVolume");
            aEvent._idProgramme = dataReader.IsDBNull(dataReader.GetOrdinal("idProgramme")) ? 0 : dataReader.GetUInt64("idProgramme");
            aEvent._idAux = dataReader.IsDBNull(dataReader.GetOrdinal("IdAux")) ? default(string) : dataReader.GetString("IdAux");
            aEvent._enabled = (flags & (1 << 0)) != 0;
            aEvent._hold = (flags & (1 << 1)) != 0;
            EventGPI.FromUInt64(ref aEvent._gPI, (flags >> 4) & EventGPI.Mask);
            aEvent._nextLoaded = false;
            return aEvent;
        }

        private static DateTime _minMySqlDate = new DateTime(1000, 01, 01);
        private static DateTime _maxMySQLDate = new DateTime(9999, 12, 31, 23, 59, 59);

        private static Boolean _EventFillParamsAndExecute(MySqlCommand cmd, Event aEvent)
        {
           
            Debug.WriteLineIf(aEvent._duration.Days > 1, aEvent, "Duration extremely long");
            cmd.Parameters.AddWithValue("@idEngine", aEvent.Engine.IdEngine);
            cmd.Parameters.AddWithValue("@idEventBinding", aEvent.idEventBinding);
            cmd.Parameters.AddWithValue("@Layer", (sbyte)aEvent._layer);
            cmd.Parameters.AddWithValue("@typEvent", aEvent._eventType);
            cmd.Parameters.AddWithValue("@typStart", aEvent._startType);
            if (aEvent._scheduledTime < _minMySqlDate || aEvent._scheduledTime > _maxMySQLDate)
            {
                cmd.Parameters.AddWithValue("@ScheduledTime", DBNull.Value);
                Debug.WriteLine(aEvent, "null ScheduledTime");
            }
            else
                cmd.Parameters.AddWithValue("@ScheduledTime", aEvent._scheduledTime);
            cmd.Parameters.AddWithValue("@Duration", aEvent._duration);
            if (aEvent._scheduledTC.Equals(TimeSpan.Zero))
                cmd.Parameters.AddWithValue("@ScheduledTC", DBNull.Value);
            else
                cmd.Parameters.AddWithValue("@ScheduledTC", aEvent._scheduledTC);
            cmd.Parameters.AddWithValue("@ScheduledDelay", aEvent._scheduledDelay);
            if (aEvent.MediaGuid == Guid.Empty)
                cmd.Parameters.AddWithValue("@MediaGuid", DBNull.Value);
            else
                cmd.Parameters.Add("@MediaGuid", MySqlDbType.Binary).Value = aEvent._mediaGuid.ToByteArray();
            cmd.Parameters.AddWithValue("@EventName", aEvent._eventName);
            cmd.Parameters.AddWithValue("@PlayState", aEvent._playState);
            if (aEvent._startTime < _minMySqlDate || aEvent._startTime > _maxMySQLDate)
                cmd.Parameters.AddWithValue("@StartTime", DBNull.Value);
            else
                cmd.Parameters.AddWithValue("@StartTime", aEvent._startTime);
            if (aEvent._startTC.Equals(TimeSpan.Zero))
                cmd.Parameters.AddWithValue("@StartTC", DBNull.Value);
            else
                cmd.Parameters.AddWithValue("@StartTC", aEvent._startTC);
            if (aEvent._requestedStartTime == null)
                cmd.Parameters.AddWithValue("@RequestedStartTime", DBNull.Value);
            else
                cmd.Parameters.AddWithValue("@RequestedStartTime", aEvent._requestedStartTime);
            cmd.Parameters.AddWithValue("@TransitionTime", aEvent._transitionTime);
            cmd.Parameters.AddWithValue("@typTransition", aEvent._transitionType);
            cmd.Parameters.AddWithValue("@idProgramme", aEvent._idProgramme);
            cmd.Parameters.AddWithValue("@AudioVolume", aEvent._audioVolume);
            UInt64 flags = Convert.ToUInt64(aEvent._enabled) << 0
                         | Convert.ToUInt64(aEvent._hold) << 1
                         | aEvent.GPI.ToUInt64() << 4 // of size EventGPI.Size
                         ;
            cmd.Parameters.AddWithValue("@flagsEvent", flags);
            lock (connection)
            {
               return cmd.ExecuteNonQuery() == 1;
            }
        }

        internal static Boolean DbInsert(this Event aEvent)
        {
            Boolean success = false;
            if (Connect())
            {
                Debug.WriteLine(aEvent, "Event table insert");
                string query =
@"INSERT INTO tas.RundownEvent 
(idEngine, idEventBinding, Layer, typEvent, typStart, ScheduledTime, ScheduledDelay, Duration, ScheduledTC, MediaGuid, EventName, PlayState, StartTime, StartTC, RequestedStartTime, TransitionTime, typTransition, AudioVolume, idProgramme, flagsEvent) 
VALUES 
(@idEngine, @idEventBinding, @Layer, @typEvent, @typStart, @ScheduledTime, @ScheduledDelay, @Duration, @ScheduledTC, @MediaGuid, @EventName, @PlayState, @StartTime, @StartTC, @RequestedStartTime, @TransitionTime, @typTransition, @AudioVolume, @idProgramme, @flagsEvent);";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                success = _EventFillParamsAndExecute(cmd, aEvent);
                aEvent.IdRundownEvent = (UInt64) cmd.LastInsertedId;
            }
            return success;
        }

        internal static Boolean DbUpdate(this Event aEvent)
        {
            if (Connect())
            {
                Debug.WriteLine(aEvent, "Event table update");
                string query =
@"UPDATE tas.RundownEvent 
SET 
idEngine=@idEngine, 
idEventBinding=@idEventBinding, 
Layer=@Layer, 
typEvent=@typEvent, 
typStart=@typStart, 
ScheduledTime=@ScheduledTime, 
ScheduledDelay=@ScheduledDelay, 
ScheduledTC=@ScheduledTC,
Duration=@Duration, 
MediaGuid=@MediaGuid, 
EventName=@EventName, 
PlayState=@PlayState, 
StartTime=@StartTime, 
StartTC=@StartTC,
RequestedStartTime=@RequestedStartTime,
TransitionTime=@TransitionTime, 
typTransition=@typTransition, 
AudioVolume=@AudioVolume, 
idProgramme=@idProgramme, 
flagsEvent=@flagsEvent 
WHERE idRundownEvent=@idRundownEvent;"; 
                MySqlCommand cmd = new MySqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@idRundownEvent", aEvent.IdRundownEvent);
                return _EventFillParamsAndExecute(cmd, aEvent);
            }
            return false;
        }

        internal static Boolean DbDelete(this Event aEvent)
        {
            Boolean success = false;
            if (Connect())
            {
                string query = "DELETE FROM tas.RundownEvent WHERE idRundownEvent=@idRundownEvent;";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@idRundownEvent", aEvent.IdRundownEvent);
                lock (connection)
                {
                    cmd.ExecuteNonQuery();
                }
                success = true;
                Debug.WriteLine(aEvent, "Deleted");
            }
            return success;
        }

        private static Boolean _mediaFillParamsAndExecute(MySqlCommand cmd, PersistentMedia media)
        {
            cmd.Parameters.AddWithValue("@idProgramme", media.idProgramme);
            cmd.Parameters.AddWithValue("@idFormat", media.idFormat);
            cmd.Parameters.AddWithValue("@idAux", media.idAux);
            if (media.MediaGuid == Guid.Empty)
                cmd.Parameters.AddWithValue("@MediaGuid", DBNull.Value);
            else
                cmd.Parameters.Add("@MediaGuid", MySqlDbType.Binary).Value = media.MediaGuid.ToByteArray();
            if (media.KillDate == default(DateTime))
                cmd.Parameters.AddWithValue("@KillDate", DBNull.Value);
            else
                cmd.Parameters.AddWithValue("@KillDate", media.KillDate);
            uint flags = ((media is ServerMedia && (media as ServerMedia).DoNotArchive) ? (uint)0x1 : (uint)0x0)
                        | ((uint)(media.MediaCategory) << 4) // bits 4-7 of 1st byte
                        | ((uint)media.MediaEmphasis << 8) // bits 1-3 of second byte
                        | ((uint)media.Parental << 12) // bits 4-7 of second byte
                        ;
            cmd.Parameters.AddWithValue("@flags", flags);
            if (media is ServerMedia && media.Directory is ServerDirectory)
            {
                cmd.Parameters.AddWithValue("@idServer", ((media as ServerMedia).Directory as ServerDirectory).Server.idServer);
                cmd.Parameters.AddWithValue("@typVideo", ((byte)media.VideoFormat) | (media.HasExtraLines ? (byte)0x80 : (byte)0x0));
            }
            if (media is ServerMedia && media.Directory is AnimationDirectory)
            {
                cmd.Parameters.AddWithValue("@idServer", ((media as ServerMedia).Directory as AnimationDirectory).Server.idServer);
                cmd.Parameters.AddWithValue("@typVideo", DBNull.Value);
            }
            if (media is ArchiveMedia && media.Directory is ArchiveDirectory)
            {
                cmd.Parameters.AddWithValue("@idArchive", (((media as ArchiveMedia).Directory) as ArchiveDirectory).IdArchive);
                cmd.Parameters.AddWithValue("@typVideo", (byte)media.VideoFormat);
            }
            cmd.Parameters.AddWithValue("@MediaName", media.MediaName);
            cmd.Parameters.AddWithValue("@Duration", media.Duration);
            cmd.Parameters.AddWithValue("@DurationPlay", media.DurationPlay);
            cmd.Parameters.AddWithValue("@Folder", media.Folder);
            cmd.Parameters.AddWithValue("@FileSize", media.FileSize);
            cmd.Parameters.AddWithValue("@FileName", media.FileName);
            if (media.LastUpdated == default(DateTime))
                cmd.Parameters.AddWithValue("@LastUpdated", DBNull.Value);
            else
                cmd.Parameters.AddWithValue("@LastUpdated", media.LastUpdated);
            cmd.Parameters.AddWithValue("@statusMedia", (Int32)media.MediaStatus);
            cmd.Parameters.AddWithValue("@typMedia", (Int32)media.MediaType);
            cmd.Parameters.AddWithValue("@typAudio", (byte)media.AudioChannelMapping);
            cmd.Parameters.AddWithValue("@AudioVolume", media.AudioVolume);
            cmd.Parameters.AddWithValue("@AudioLevelIntegrated", media.AudioLevelIntegrated);
            cmd.Parameters.AddWithValue("@AudioLevelPeak", media.AudioLevelPeak);
            cmd.Parameters.AddWithValue("@TCStart", media.TCStart);
            cmd.Parameters.AddWithValue("@TCPlay", media.TCPlay);
            lock (connection)
            {
                try
                {
                    cmd.ExecuteNonQuery();
                }
                catch (Exception e)
                { Debug.WriteLine(media, e.Message); }
            }
            return true;
        }    

        private static void _mediaReadFields(MySqlDataReader dataReader, PersistentMedia media)
        {
            uint flags = dataReader.IsDBNull(dataReader.GetOrdinal("flags")) ? (uint)0 : dataReader.GetUInt32("flags");
            byte typVideo = dataReader.IsDBNull(dataReader.GetOrdinal("typVideo")) ? (byte)0 : dataReader.GetByte("typVideo");
            media._mediaName = dataReader.IsDBNull(dataReader.GetOrdinal("MediaName")) ? string.Empty : dataReader.GetString("MediaName");
            media._duration = dataReader.IsDBNull(dataReader.GetOrdinal("Duration")) ? default(TimeSpan) : dataReader.GetTimeSpan("Duration");
            media._durationPlay = dataReader.IsDBNull(dataReader.GetOrdinal("DurationPlay")) ? default(TimeSpan) : dataReader.GetTimeSpan("DurationPlay");
            media._folder = dataReader.IsDBNull(dataReader.GetOrdinal("Folder")) ? string.Empty : dataReader.GetString("Folder");
            media._fileName = dataReader.IsDBNull(dataReader.GetOrdinal("FileName")) ? string.Empty : dataReader.GetString("FileName");
            media._fileSize = dataReader.IsDBNull(dataReader.GetOrdinal("FileSize")) ? 0 : dataReader.GetUInt64("FileSize");
            media.idFormat = dataReader.IsDBNull(dataReader.GetOrdinal("idFormat")) ? 0 : dataReader.GetUInt64("idFormat");
            media._lastUpdated = _readDateTimeField(dataReader, "LastUpdated");
            media._mediaStatus = (TMediaStatus)(dataReader.IsDBNull(dataReader.GetOrdinal("statusMedia")) ? 0 : dataReader.GetInt32("statusMedia"));
            media._mediaType = (TMediaType)(dataReader.IsDBNull(dataReader.GetOrdinal("typMedia")) ? 0 : dataReader.GetInt32("typMedia"));
            media._tCStart = dataReader.IsDBNull(dataReader.GetOrdinal("TCStart")) ? default(TimeSpan) : dataReader.GetTimeSpan("TCStart");
            media._tCPlay = dataReader.IsDBNull(dataReader.GetOrdinal("TCPlay")) ? default(TimeSpan) : dataReader.GetTimeSpan("TCPlay");
            media.idProgramme = dataReader.IsDBNull(dataReader.GetOrdinal("idProgramme")) ? 0 : dataReader.GetUInt64("idProgramme");
            media._audioVolume = dataReader.IsDBNull(dataReader.GetOrdinal("AudioVolume")) ? 0 : dataReader.GetDecimal("AudioVolume");
            media._audioLevelIntegrated = dataReader.IsDBNull(dataReader.GetOrdinal("AudioLevelIntegrated")) ? 0 : dataReader.GetDecimal("AudioLevelIntegrated");
            media._audioLevelPeak = dataReader.IsDBNull(dataReader.GetOrdinal("AudioLevelPeak")) ? 0 : dataReader.GetDecimal("AudioLevelPeak");
            media._audioChannelMapping = dataReader.IsDBNull(dataReader.GetOrdinal("typAudio")) ? TAudioChannelMapping.Stereo : (TAudioChannelMapping)dataReader.GetByte("typAudio");
            media.HasExtraLines = (typVideo & (byte)0x80) > 0;
            media._videoFormat = (TVideoFormat)(typVideo & 0x7F);
            media._idAux = dataReader.IsDBNull(dataReader.GetOrdinal("idAux")) ? string.Empty : dataReader.GetString("idAux");
            media._killDate = _readDateTimeField(dataReader, "KillDate");
            media._mediaGuid = dataReader.IsDBNull(dataReader.GetOrdinal("MediaGuid")) ? Guid.Empty : dataReader.GetGuid("MediaGuid");
            media._mediaEmphasis = (TMediaEmphasis)((flags >> 8) & 0xF);
            media._parental = (TParental)((flags >> 12) & 0xF);
            if (media is ServerMedia)
                ((ServerMedia)media)._doNotArchive = (flags & 0x1) != 0;
            media._mediaCategory = (TMediaCategory)((flags >> 4) & 0xF); // bits 4-7 of 1st byte

        }

        internal static void ServerLoadMediaDirectory(AnimationDirectory directory, PlayoutServer server)
        {
            Debug.WriteLine(directory, "ServerLoadMediaDirectory animation started");
            if (Connect())
            {
                MySqlCommand cmd = new MySqlCommand("SELECT * FROM tas.serverMedia WHERE idServer=@idServer and typMedia = @typMedia", connection);
                cmd.Parameters.AddWithValue("@idServer", server.idServer);
                cmd.Parameters.AddWithValue("@typMedia", TMediaType.AnimationFlash);
                try
                {
                    lock (connection)
                    {
                        using (MySqlDataReader dataReader = cmd.ExecuteReader())
                        {
                            while (dataReader.Read())
                            {
                                ServerMedia nm = new ServerMedia()
                                {
                                    idPersistentMedia = dataReader.GetUInt64("idServerMedia"),
                                    Directory = directory,
                                };
                                _mediaReadFields(dataReader, nm);
                                if (nm.MediaStatus != TMediaStatus.Available)
                                {
                                    nm.MediaStatus = TMediaStatus.Unknown;
                                    ThreadPool.QueueUserWorkItem(o => nm.Verify());
                                }
                            }
                            dataReader.Close();
                        }
                    }
                    Debug.WriteLine(directory, "Directory loaded");
                }
                catch (Exception e)
                {
                    Debug.WriteLine(directory, e.Message);
                }
            }
        }

        internal static void Load(this ServerDirectory directory)
        {
            Debug.WriteLine(directory, "ServerLoadMediaDirectory started");
            if (Connect())
            {
                MySqlCommand cmd = new MySqlCommand("SELECT * FROM tas.serverMedia WHERE idServer=@idServer and typMedia in (@typMediaMovie, @typMediaStill)", connection);
                cmd.Parameters.AddWithValue("@idServer", directory.Server.idServer);
                cmd.Parameters.AddWithValue("@typMediaMovie", TMediaType.Movie);
                cmd.Parameters.AddWithValue("@typMediaStill", TMediaType.Still);
                try
                {
                    lock (connection)
                    {
                        using (MySqlDataReader dataReader = cmd.ExecuteReader())
                        {
                            while (dataReader.Read())
                            {
                                ServerMedia nm = new ServerMedia()
                                {
                                    idPersistentMedia = dataReader.GetUInt64("idServerMedia"),
                                    Directory = directory,
                                };
                                _mediaReadFields(dataReader, nm);
                                if (nm.MediaStatus != TMediaStatus.Available)
                                {
                                    nm.MediaStatus = TMediaStatus.Unknown;
                                    ThreadPool.QueueUserWorkItem(o => nm.Verify());
                                }
                            }
                            dataReader.Close();
                        }
                    }
                    Debug.WriteLine(directory, "Directory loaded");
                }
                catch (Exception e)
                {
                    Debug.WriteLine(directory, e.Message);
                }
            }
        }

        internal static Boolean DbInsert(this ServerMedia serverMedia)
        {
            Boolean success = false;
            if (Connect())
            {
                string query = 
@"INSERT INTO tas.servermedia 
(idServer, MediaName, Folder, FileName, FileSize, LastUpdated, Duration, DurationPlay, idProgramme, statusMedia, typMedia, idFormat, typAudio, typVideo, TCStart, TCPlay, AudioVolume, AudioLevelIntegrated, AudioLevelPeak, idAux, KillDate, MediaGuid, flags) 
VALUES 
(@idServer, @MediaName, @Folder, @FileName, @FileSize, @LastUpdated, @Duration, @DurationPlay, @idProgramme, @statusMedia, @typMedia, @idFormat, @typAudio, @typVideo, @TCStart, @TCPlay, @AudioVolume, @AudioLevelIntegrated, @AudioLevelPeak, @idAux, @KillDate, @MediaGuid, @flags);";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                _mediaFillParamsAndExecute(cmd, serverMedia);
                serverMedia.idPersistentMedia = (UInt64)cmd.LastInsertedId;
                success = true;
                Debug.WriteLineIf(success, serverMedia, "ServerMediaInserte-d");
            }
            return success;
        }
        
        internal static Boolean DbInsert(this ArchiveMedia archiveMedia)
        {
            Boolean success = false;
            if (Connect())
            {
                string query =
@"INSERT INTO tas.archivemedia 
(idArchive, MediaName, Folder, FileName, FileSize, LastUpdated, Duration, DurationPlay, idProgramme, statusMedia, typMedia, idFormat, typAudio, typVideo, TCStart, TCPlay, AudioVolume, AudioLevelIntegrated, AudioLevelPeak, idAux, KillDate, MediaGuid, flags) 
VALUES 
(@idArchive, @MediaName, @Folder, @FileName, @FileSize, @LastUpdated, @Duration, @DurationPlay, @idProgramme, @statusMedia, @typMedia, @idFormat, @typAudio, @typVideo, @TCStart, @TCPlay, @AudioVolume, @AudioLevelIntegrated, @AudioLevelPeak, @idAux, @KillDate, @MediaGuid, @flags);";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                _mediaFillParamsAndExecute(cmd, archiveMedia);
                archiveMedia.idPersistentMedia = (UInt64)cmd.LastInsertedId;
                success = true;
            }
            return success;
        }

        internal static Boolean DbDelete(this ServerMedia serverMedia)
        {
            if (Connect())
            {
                string query = "DELETE FROM tas.ServerMedia WHERE idServerMedia=@idServerMedia;";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@idServerMedia", serverMedia.idPersistentMedia);
                lock (connection)
                {
                    return cmd.ExecuteNonQuery() == 1;
                }
            }
            return false;
        }

        internal static Boolean DbDelete(this ArchiveMedia archiveMedia)
        {
            Boolean success = false;
            if (Connect())
            {
                string query = "DELETE FROM tas.archivemedia WHERE idArchiveMedia=@idArchiveMedia;";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@idArchiveMedia", archiveMedia.idPersistentMedia);
                lock (connection)
                {
                    return cmd.ExecuteNonQuery() == 1;
                }
            }
            return false;
        }
        
        internal static Boolean DbUpdate(this ServerMedia serverMedia)
        {
            Boolean success = false;
            if (Connect())
            {
                string query =
@"UPDATE tas.ServerMedia SET 
idServer=@idServer, 
MediaName=@MediaName, 
Folder=@Folder, 
FileName=@FileName, 
FileSize=@FileSize, 
LastUpdated=@LastUpdated, 
Duration=@Duration, 
DurationPlay=@DurationPlay, 
idProgramme=@idProgramme, 
statusMedia=@statusMedia, 
typMedia=@typMedia, 
idFormat=@idFormat, 
typAudio=@typAudio, 
typVideo=@typVideo, 
TCStart=@TCStart, 
TCPlay=@TCPlay, 
AudioVolume=@AudioVolume, 
AudioLevelIntegrated=@AudioLevelIntegrated,
AudioLevelPeak=@AudioLevelPeak,
idAux=@idAux, 
KillDate=@KillDate, 
MediaGuid=@MediaGuid, 
flags=@flags 
WHERE idServerMedia=@idServerMedia;";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@idServerMedia", serverMedia.idPersistentMedia);
                success = _mediaFillParamsAndExecute(cmd, serverMedia);
                Debug.WriteLineIf(success, serverMedia, "ServerMediaUpdate-d");
            }
            return success;
        }

        internal static Boolean DbUpdate(this ArchiveMedia archiveMedia)
        {
            Boolean success = false;
            if (Connect())
            {
                string query =
@"UPDATE tas.archivemedia SET 
idArchive=@idArchive, 
MediaName=@MediaName, 
Folder=@Folder, 
FileName=@FileName, 
FileSize=@FileSize, 
LastUpdated=@LastUpdated, 
Duration=@Duration, 
DurationPlay=@DurationPlay, 
idProgramme=@idProgramme, 
statusMedia=@statusMedia, 
typMedia=@typMedia, 
idFormat=@idFormat, 
typAudio=@typAudio, 
typVideo=@typVideo, 
TCStart=@TCStart, 
TCPlay=@TCPlay, 
AudioVolume=@AudioVolume, 
AudioLevelIntegrated=@AudioLevelIntegrated,
AudioLevelPeak=@AudioLevelPeak,
idAux=@idAux, 
KillDate=@KillDate, 
MediaGuid=@MediaGuid, 
flags=@flags 
WHERE idArchiveMedia=@idArchiveMedia;";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@idArchiveMedia", archiveMedia.idPersistentMedia);
                success = _mediaFillParamsAndExecute(cmd, archiveMedia);
                Debug.WriteLineIf(success, archiveMedia, "ArchiveMediaUpdate-d");
            }
            return success;
        }

        internal static bool DbMediaInUse(this ServerMedia serverMedia)
        {
            Boolean IsInUse = true;
            if (Connect())
            {
                string query = "select count(*) from tas.rundownevent where MediaGuid=@MediaGuid and ADDTIME(ScheduledTime, Duration) > UTC_TIMESTAMP();";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                cmd.Parameters.Add("@MediaGuid", MySqlDbType.Binary).Value = serverMedia.MediaGuid.ToByteArray();
                lock(connection)
                    IsInUse = (long)cmd.ExecuteScalar() > 0;
            }
            return IsInUse;
        }

        private static ArchiveMedia _readArchiveMedia(MySqlDataReader dataReader, ArchiveDirectory dir)
        {
            byte typVideo = dataReader.IsDBNull(dataReader.GetOrdinal("typVideo")) ? (byte)0 : dataReader.GetByte("typVideo");
            ArchiveMedia media = new ArchiveMedia()
                {
                    idPersistentMedia = dataReader.GetUInt64("idArchiveMedia"),
                    Directory = dir,
                };
            _mediaReadFields(dataReader, media);
            ThreadPool.QueueUserWorkItem(o => media.Verify());
            return media;
        }

        internal static void DbSearch(this ArchiveDirectory dir)
        {
            string search = dir.SearchString;
            if (string.IsNullOrWhiteSpace(search))
                return;
            dir.Clear();
            if (Connect())
            {
                var textSearches = from text in search.ToLower().Split(' ').Where(s => !string.IsNullOrEmpty(s)) select "(LOWER(MediaName) LIKE \"%" + text + "%\" or LOWER(FileName) LIKE \"%" + text + "%\")";
                MySqlCommand cmd;
                if (dir.SearchMediaCategory == null)
                    cmd = new MySqlCommand(@"SELECT * FROM tas.archivemedia WHERE idArchive=@idArchive and " + string.Join(" and ", textSearches) + " LIMIT 0, 1000;", connection);
                else
                {
                    cmd = new MySqlCommand(@"SELECT * FROM tas.archivemedia WHERE idArchive=@idArchive and ((flags >> 4) & 3)=@Category and  " + string.Join(" and ", textSearches) + " LIMIT 0, 1000;", connection);
                    cmd.Parameters.AddWithValue("@Category", (uint)dir.SearchMediaCategory);
                }
                cmd.Parameters.AddWithValue("@idArchive", dir.IdArchive);
                lock (connection)
                {
                    using (MySqlDataReader dataReader = cmd.ExecuteReader())
                    {
                        while (dataReader.Read())
                            _readArchiveMedia(dataReader, dir);
                        dataReader.Close();
                    }
                }
            }
        }

        internal static void AsRunLogWrite(this Event e)
        {
            try
            {
                if (Connect())
                {
                    MySqlCommand cmd = new MySqlCommand(
@"INSERT INTO asrunlog (
ExecuteTime, 
MediaName, 
StartTC,
Duration,
idProgramme, 
idAuxMedia, 
idAuxRundown, 
SecEvents, 
typVideo, 
typAudio
)
VALUES
(
@ExecuteTime, 
@MediaName, 
@StartTC,
@Duration,
@idProgramme, 
@idAuxMedia, 
@idAuxRundown, 
@SecEvents, 
@typVideo, 
@typAudio
);", connection);
                    cmd.Parameters.AddWithValue("@ExecuteTime", e.StartTime);
                    Media media = e.Media;
                    if (media != null)
                    {
                        cmd.Parameters.AddWithValue("@MediaName", media.MediaName);
                        if (media is PersistentMedia)
                            cmd.Parameters.AddWithValue("@idAuxMedia", (media as PersistentMedia).idAux);
                        else
                            cmd.Parameters.AddWithValue("@idAuxMedia", DBNull.Value);
                        cmd.Parameters.AddWithValue("@typVideo", (byte)media.VideoFormat);
                        cmd.Parameters.AddWithValue("@typAudio", (byte)media.AudioChannelMapping);
                    }
                    else
                    {
                        if (e.EventType == TEventType.Live)
                            cmd.Parameters.AddWithValue("@MediaName", "LIVE");
                        else
                            cmd.Parameters.AddWithValue("@MediaName", DBNull.Value);
                        cmd.Parameters.AddWithValue("@idAuxMedia", DBNull.Value);
                        cmd.Parameters.AddWithValue("@typVideo", DBNull.Value);
                        cmd.Parameters.AddWithValue("@typAudio", DBNull.Value);
                    }
                    cmd.Parameters.AddWithValue("@StartTC", e.StartTC);
                    cmd.Parameters.AddWithValue("@Duration", e.Duration);
                    cmd.Parameters.AddWithValue("@idProgramme", e.idProgramme);
                    cmd.Parameters.AddWithValue("@idAuxRundown", e.IdAux);
                    cmd.Parameters.AddWithValue("@SecEvents", string.Join(";", e.SubEvents.ToList().Select(se => se.EventName)));
                    lock (connection)
                        cmd.ExecuteNonQuery();
                }
                Debug.WriteLine(e, "AsRunLog written for");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        internal static IEnumerable<ArchiveMedia> DbFindStaleMedia(this ArchiveDirectory dir)
        {
            List<ArchiveMedia> returnList = new List<ArchiveMedia>();
            if (Connect())
            {
                MySqlCommand cmd = new MySqlCommand(@"SELECT * FROM tas.archivemedia WHERE idArchive=@idArchive and KillDate<CURRENT_DATE and KillDate>'2000-01-01' LIMIT 0, 1000;", connection);
                cmd.Parameters.AddWithValue("@idArchive", dir.IdArchive);
                lock (connection)
                {
                    using (MySqlDataReader dataReader = cmd.ExecuteReader())
                    {
                        while (dataReader.Read())
                            returnList.Add(_readArchiveMedia(dataReader, dir));
                        dataReader.Close();
                    }
                }
            }
            return returnList;
        }

        internal static ArchiveMedia DbMediaFind(this ArchiveDirectory dir, Media media)
        {
            ArchiveMedia result = null;
            if (Connect())
            {
                MySqlCommand cmd;
                if (media.MediaGuid != Guid.Empty)
                {
                    cmd = new MySqlCommand("SELECT * FROM tas.archivemedia WHERE idArchive=@idArchive && MediaGuid=@MediaGuid;", connection);
                    cmd.Parameters.AddWithValue("@idArchive", dir.IdArchive);
                    cmd.Parameters.Add("@MediaGuid", MySqlDbType.Binary).Value = media.MediaGuid.ToByteArray();
                    lock (connection)
                    {
                        using (MySqlDataReader dataReader = cmd.ExecuteReader(CommandBehavior.SingleRow))
                        {
                            if (dataReader.Read())
                                result = _readArchiveMedia(dataReader, dir);
                            dataReader.Close();
                        }
                    }
                }
            }
            return result;
        }

        internal static ArchiveDirectory LoadArchiveDirectory(UInt64 idArchive)
        {
                if (Connect())
                {
                    string query = "SELECT Folder FROM archive WHERE idArchive=@idArchive;";
                    MySqlCommand cmd = new MySqlCommand(query, connection);
                    cmd.Parameters.AddWithValue("@idArchive", idArchive);
                    string folder = null;
                    lock (connection)
                    {
                        folder = (string)cmd.ExecuteScalar();
                    }
                    if (!string.IsNullOrEmpty(folder))
                    {
                        ArchiveDirectory directory = new ArchiveDirectory()
                        {
                            IdArchive = idArchive,
                            Folder = folder,
                        };
                        directory.Initialize();
                        return directory;
                    }
                }
            return null;
        }

        internal static bool FileExists(this ArchiveDirectory dir, string fileName)
        {
            if (Connect())
            {
                string query = "SELECT COUNT(*) FROM tas.archivemedia WHERE idArchive=@idArchive AND FileName=@FileName AND Folder=@Folder;";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@idArchive", dir.IdArchive);
                cmd.Parameters.AddWithValue("@FileName", fileName);
                cmd.Parameters.AddWithValue("@Folder", dir.GetCurrentFolder());
                lock (connection)
                    return (long)cmd.ExecuteScalar() != 0;
            }
            return true;
        }

        internal static List<Engine> DbLoadEngines(UInt64 instance, List<PlayoutServer> servers)
        {
            List<Engine> Engines = new List<Engine>();
            if (Connect())
            {
                MySqlCommand cmd = new MySqlCommand("SELECT * FROM tas.Engine where Instance=@Instance;", connection);
                cmd.Parameters.AddWithValue("Instance", instance);
                lock (connection)
                {
                    using (MySqlDataReader dataReader = cmd.ExecuteReader())
                    {
                        while (dataReader.Read())
                        {
                            UInt64 idServerPGM = dataReader.IsDBNull(dataReader.GetOrdinal("idServerPGM")) ? 0UL : dataReader.GetUInt64("idServerPGM");
                            int numServerChannelPGM = dataReader.IsDBNull(dataReader.GetOrdinal("ServerChannelPGM")) ? 0 : dataReader.GetInt32("ServerChannelPGM"); 
                            UInt64 idServerPRV = dataReader.IsDBNull(dataReader.GetOrdinal("idServerPRV")) ? 0UL : dataReader.GetUInt64("idServerPRV");
                            int numServerChannelPRV = dataReader.IsDBNull(dataReader.GetOrdinal("ServerChannelPRV")) ? 0 : dataReader.GetInt32("ServerChannelPRV"); 

                            var sPGM = servers.Find(S => S.idServer == idServerPGM);
                            var cPGM = sPGM == null || sPGM.Channels.Count > numServerChannelPGM - 1 ? sPGM.Channels[numServerChannelPGM - 1] : null;
                            if (cPGM != null)
                                sPGM.MediaDirectory.DirectoryName = "PGM";
                            var sPRV = servers.Find(S => S.idServer == idServerPRV);
                            var cPRV = sPRV == null || sPRV.Channels.Count > numServerChannelPRV - 1 ? sPRV.Channels[numServerChannelPRV - 1] : null;
                            if (cPRV != null)
                                sPRV.MediaDirectory.DirectoryName = "PRV";
                            Engine newEngine = SerializationHelper.Deserialize<Engine>(dataReader.GetString("Config"));
                            newEngine.IdEngine = dataReader.GetUInt64("idEngine");
                            newEngine.Instance = dataReader.GetUInt64("Instance");
                            newEngine.PlayoutChannelPGM = cPGM;
                            newEngine.PlayoutChannelPRV = cPRV;
                            newEngine.idArchive = dataReader.GetUInt64("idArchive");
                            Engines.Add(newEngine);
                        }
                        dataReader.Close();
                    }
                }
            } 
            return Engines;
        }

        internal static bool DbUpdate(this Engine engine)
        {
            if (Connect())
            {
                MySqlCommand cmd = new MySqlCommand(
@"UPDATE tas.Engine set 
Instance=@Instance, 
idServerPGM=@idServerPGM, 
ServerChannelPGM=@ServerChannelPGM, 
idServerPRV=@idServerPRV, 
ServerChannelPRV=@ServerChannelPRV,
idArchive=@idArchive, 
Config=@Config
where
idEngine=@idEngine", connection);
                cmd.Parameters.AddWithValue("@idEngine", engine.IdEngine);
                cmd.Parameters.AddWithValue("@Instance", engine.Instance);
                cmd.Parameters.AddWithValue("@idServerPGM", engine.PlayoutChannelPGM == null ? DBNull.Value : (object)engine.PlayoutChannelPGM.OwnerServer.idServer);
                cmd.Parameters.AddWithValue("@ServerChannelPGM", engine.PlayoutChannelPGM == null ? DBNull.Value : (object)engine.PlayoutChannelPGM.ChannelNumber);
                cmd.Parameters.AddWithValue("@idServerPRV", engine.PlayoutChannelPRV == null ? DBNull.Value : (object)engine.PlayoutChannelPRV.OwnerServer.idServer);
                cmd.Parameters.AddWithValue("@ServerChannelPRV", engine.PlayoutChannelPRV == null ? DBNull.Value : (object)engine.PlayoutChannelPRV.ChannelNumber);
                cmd.Parameters.AddWithValue("@idArchive", engine.idArchive);
                cmd.Parameters.AddWithValue("@Config", SerializationHelper.Serialize<Engine>(engine));
                
                lock (connection)
                    if (cmd.ExecuteNonQuery() == 1)
                    {
                        Debug.WriteLine(engine, "Saved");
                        return true;
                    }
            }
            return false;
        }

        internal static List<PlayoutServer> DbLoadServers()
        {
            Debug.WriteLine("Begin loading servers");
            List<PlayoutServer> servers = new List<PlayoutServer>();
            if (Connect())
            {
                MySqlCommand cmd = new MySqlCommand("SELECT * FROM Server;", connection);
                lock (connection)
                {
                    using (MySqlDataReader dataReader = cmd.ExecuteReader())
                    {
                        while (dataReader.Read())
                        {
                            switch ((TServerType)dataReader.GetInt32("typServer"))
                            {
                                case TServerType.Caspar:
                                    {
                                        Debug.WriteLine("Adding Caspar server");
                                        string serverParams = dataReader.GetString("Config");
                                        CasparServer newServer = SerializationHelper.Deserialize<CasparServer>(serverParams);
                                        XmlDocument configXml = new XmlDocument();
                                        configXml.Load(new StringReader(serverParams));
                                        newServer.idServer = dataReader.GetUInt64("idServer");
                                        XmlNode channelsNode = configXml.SelectSingleNode(@"CasparServer/Channels");
                                        Debug.WriteLine("Adding Caspar channels");
                                        newServer.Channels = SerializationHelper.Deserialize<List<CasparServerChannel>>(channelsNode.OuterXml, "Channels").ConvertAll<PlayoutServerChannel>(pc => (PlayoutServerChannel)pc);
                                        servers.Add(newServer);
                                        Debug.WriteLine("Caspar server added");
                                        break;
                                    }
                            }
                        }
                        dataReader.Close();
                    }
                }
            } 
            return servers;
        }

        private static Hashtable _mediaSegments;

        internal static ObservableSynchronizedCollection<MediaSegment> DbMediaSegmentsRead(this PersistentMedia media)
        {
            if (Connect())
            {
                Guid mediaGuid = media.MediaGuid;
                ObservableSynchronizedCollection<MediaSegment> segments = null;
                MediaSegment newMediaSegment;
                MySqlCommand cmd = new MySqlCommand("SELECT * FROM tas.MediaSegments where MediaGuid = @MediaGuid;", connection);
                cmd.Parameters.Add("@MediaGuid", MySqlDbType.Binary).Value = mediaGuid.ToByteArray();
                lock (connection)
                {
                    if (_mediaSegments == null)
                        _mediaSegments = new Hashtable();
                    segments = (ObservableSynchronizedCollection<MediaSegment>)_mediaSegments[mediaGuid];
                    if (segments == null)
                    {
                        segments = new ObservableSynchronizedCollection<MediaSegment>();
                        using (MySqlDataReader dataReader = cmd.ExecuteReader())
                        {
                            while (dataReader.Read())
                            {
                                newMediaSegment = new MediaSegment(mediaGuid)
                                {
                                    idMediaSegment = dataReader.GetUInt64("idMediaSegment"),
                                    SegmentName = (dataReader.IsDBNull(dataReader.GetOrdinal("SegmentName")) ? string.Empty : dataReader.GetString("SegmentName")),
                                    TCIn = dataReader.IsDBNull(dataReader.GetOrdinal("TCIn")) ? default(TimeSpan) : dataReader.GetTimeSpan("TCIn"),
                                    TCOut = dataReader.IsDBNull(dataReader.GetOrdinal("TCOut")) ? default(TimeSpan) : dataReader.GetTimeSpan("TCOut"),
                                };
                                segments.Add(newMediaSegment);
                            }
                            dataReader.Close();
                        }
                        _mediaSegments.Add(mediaGuid, segments);
                    }
                }
                return segments;
            }
            else
                return null;
        }

        internal static void DbDelete(this MediaSegment mediaSegment)
        {
            if (mediaSegment.idMediaSegment != 0)
            {
                var segments = (ObservableSynchronizedCollection<MediaSegment>)_mediaSegments[mediaSegment.MediaGuid];
                if (segments != null)
                {
                    segments.Remove(mediaSegment);
                }
                if (Connect())
                {
                    string query = "DELETE FROM tas.mediasegments WHERE idMediaSegment=@idMediaSegment;";
                    MySqlCommand cmd = new MySqlCommand(query, connection);
                    cmd.Parameters.AddWithValue("@idMediaSegment", mediaSegment.idMediaSegment);
                    lock (connection)
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        internal static void DbSave(this MediaSegment mediaSegment)
        {
            if (Connect())
            {
                MySqlCommand command;
                if (mediaSegment.idMediaSegment == 0)
                    command = new MySqlCommand("INSERT INTO tas.mediasegments (MediaGuid, TCIn, TCOut, SegmentName) VALUES (@MediaGuid, @TCIn, @TCOut, @SegmentName);", connection);
                else
                {
                    command = new MySqlCommand("UPDATE tas.mediasegments SET TCIn = @TCIn, TCOut = @TCOut, SegmentName = @SegmentName WHERE idMediaSegment=@idMediaSegment AND MediaGuid = @MediaGuid;", connection);
                    command.Parameters.AddWithValue("@idMediaSegment", mediaSegment.idMediaSegment);
                }
                command.Parameters.Add("@MediaGuid", MySqlDbType.Binary).Value = mediaSegment.MediaGuid.ToByteArray();
                command.Parameters.AddWithValue("@TCIn", mediaSegment.TCIn);
                command.Parameters.AddWithValue("@TCOut", mediaSegment.TCOut);
                command.Parameters.AddWithValue("@SegmentName", mediaSegment.SegmentName);
                lock (connection)
                {
                    command.ExecuteNonQuery();
                    if (mediaSegment.idMediaSegment == 0)
                        mediaSegment.idMediaSegment = (UInt64)command.LastInsertedId;
                }
            }
        }


        internal static void DbSave(this Template template)
        {
            MySqlCommand cmd;
            bool isInsert = template.idTemplate == 0;
            if (isInsert)
            {
                cmd = new MySqlCommand(
@"INSERT INTO tas.template 
(idEngine, MediaGuid, Layer, TemplateName, TemplateFields) 
values 
(@idEngine,@MediaGuid, @Layer, @TemplateName, @TemplateFields)", connection);
                cmd.Parameters.AddWithValue("@idEngine", template.Engine.IdEngine);
            }
            else
            {
                cmd = new MySqlCommand(
@"UPDATE tas.template SET 
MediaGuid=@MediaGuid, 
Layer=@Layer, 
TemplateName=@TemplateName, 
TemplateFields=@TemplateFields 
WHERE idTemplate=@idTemplate"
                , connection);
                cmd.Parameters.AddWithValue("@idTemplate", template.idTemplate);
            }
            cmd.Parameters.Add("@MediaGuid", MySqlDbType.Binary).Value = template.MediaGuid.ToByteArray();
            cmd.Parameters.AddWithValue("@Layer", template.Layer);
            cmd.Parameters.AddWithValue("@TemplateName", template.TemplateName);
            cmd.Parameters.AddWithValue("@TemplateFields", SerializationHelper.Serialize<List<KeyValuePair<string, string>>>(template.TemplateFields));
            lock (connection)
            {
                cmd.ExecuteNonQuery();
                if (isInsert)
                    template.idTemplate = (UInt64)cmd.LastInsertedId;
            }
        }

        internal static void DbDelete(this Template template)
        {
            if (template.idTemplate == 0)
                return;
            if (Connect())
            {
                string query = "DELETE FROM tas.template WHERE idTemplate=@idTemplate;";
                MySqlCommand cmd = new MySqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@idTemplate", template.idTemplate);
                lock (connection)
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        internal static void DbReadTemplates(this Engine engine)
        {
            if (Connect())
            {
                MySqlCommand cmd = new MySqlCommand("SELECT idTemplate, MediaGuid, Layer, TemplateName, TemplateFields from template where idEngine=@idEngine;", connection);
                cmd.Parameters.AddWithValue("idEngine", engine.IdEngine);
                lock (connection)
                {
                    using (MySqlDataReader dataReader = cmd.ExecuteReader())
                    {
                        while (dataReader.Read())
                        {
                            new Template(engine)
                            {
                                idTemplate = dataReader.GetUInt64("idTemplate"),
                                MediaGuid = (dataReader.IsDBNull(dataReader.GetOrdinal("MediaGuid"))) ? Guid.Empty : dataReader.GetGuid("MediaGuid"),
                                Layer = (dataReader.IsDBNull(dataReader.GetOrdinal("Layer"))) ? 0 : dataReader.GetInt32("Layer"),
                                TemplateName = (dataReader.IsDBNull(dataReader.GetOrdinal("TemplateName"))) ? string.Empty : dataReader.GetString("TemplateName"),
                                TemplateFields = dataReader.IsDBNull(dataReader.GetOrdinal("TemplateFields")) ? null : SerializationHelper.Deserialize<List<KeyValuePair<string, string>>>(dataReader.GetString("TemplateFields")),
                            };
                        }
                    }
                }
                Debug.WriteLine(engine, "TemplateReadTemplates readed");
            }
        }
    } 
}
