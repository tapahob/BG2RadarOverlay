using NLog;
using NLog.Targets;
using NLog.Targets.Wrappers;
using System;
using System.Collections.Generic;
using System.IO;

namespace BGOverlay
{
    public static class Logger
    {
        private static NLog.Logger logger;
        private static readonly Dictionary<string, string> errorCache = new Dictionary<string, string>();

        public static void Init()
        {
            logger = LogManager.GetCurrentClassLogger();
            
            var config = new NLog.Config.LoggingConfiguration();
            
            using (var logfile = new FileTarget("logfile") { FileName = Path.Combine(Directory.GetCurrentDirectory(), "radar.log").ToString(), DeleteOldFileOnStartup = true, Layout = "${longdate} [${level:uppercase=true}] ${message:withexception=true}" })
            {
                config.AddRule(LogLevel.Debug, LogLevel.Fatal, new AsyncTargetWrapper(logfile));
            }

            LogManager.Configuration = config;
        }

        public static void Info(string msg, params object[] args)
        {            
            logger.Info(msg, args);
        }

        public static void Debug(string msg, params object[] args)
        {
            logger.Debug(msg, args);        
        }

        public static void Error(string msg, Exception ex = null)
        {
            if (ex != null && !errorCache.ContainsKey(ex.Message + ex.StackTrace)) 
            {
                errorCache[ex.Message + ex.StackTrace] = msg;
                logger.Error(ex, msg);
            }                
        }

        public static void Fatal(string msg, Exception ex = null)
        {
            if (ex != null && !errorCache.ContainsKey(ex.Message + ex.StackTrace))
            {
                errorCache[ex.Message + ex.StackTrace] = msg;
                logger.Fatal(ex, msg);
            }
        }

        public static void flush()
        {
            LogManager.Flush();
        }
    }
}
