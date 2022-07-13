using NLog;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.IO;

namespace BGOverlay
{
    public static class Logger
    {
        private static readonly NLog.Logger logger                       = NLog.LogManager.GetCurrentClassLogger();
        private static readonly Dictionary<Exception, string> errorCache = new Dictionary<Exception, string>();

        public static void Init()
        {
            var config     = new NLog.Config.LoggingConfiguration();
            var logfile    = new FileTarget("logfile") { FileName = Path.Combine(Directory.GetCurrentDirectory(), "radar.log").ToString(), DeleteOldFileOnStartup=true, Layout = "${longdate} [${level:uppercase=true}] ${message:withexception=true}" };
            
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, logfile);

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
            if (ex != null && !errorCache.ContainsKey(ex)) 
            {
                errorCache[ex] = msg;
                logger.Error(ex, msg);
            }                
        }

        public static void Fatal(string msg, Exception ex = null)
        {
            if (ex != null && !errorCache.ContainsKey(ex))
            {
                errorCache[ex] = msg;
                logger.Fatal(ex, msg);
            }
        }

        public static void flush()
        {
            LogManager.Flush();
        }
    }
}
