﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TAS.Common.Interfaces;

namespace TAS.Server
{
    public static class PluginManager
    {

        static NLog.Logger Logger = NLog.LogManager.GetLogger(nameof(PluginManager));
        static readonly IEnumerable<IEnginePluginFactory> _enginePlugins;
        
        static PluginManager()
        {
            Logger.Debug("Creating");
            using (DirectoryCatalog catalog = new DirectoryCatalog(Path.Combine(Directory.GetCurrentDirectory(), "Plugins"), "TAS.Server.*.dll"))
            {
                var container = new CompositionContainer(catalog);
                container.ComposeExportedValue("AppSettings", ConfigurationManager.AppSettings);
                try
                {
                    _enginePlugins = container.GetExportedValues<IEnginePluginFactory>();
                }
                catch (ReflectionTypeLoadException e)
                {
                    foreach (var loaderException in e.LoaderExceptions)
                        Logger.Error(e, "Plugin load exception: {0}", loaderException);
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Plugin load failed: {0}", e);
                }
            }
        }

        public static T ComposePart<T>(this IEngine engine) 
        {
            var factory = _enginePlugins?.FirstOrDefault(f => f.Types().Any(t => typeof(T).IsAssignableFrom(t)));
            if (factory != null)
                return (T)factory.CreateEnginePlugin(engine, typeof(T));
            return default(T);
        }

        public static IEnumerable<T> ComposeParts<T>(this IEngine engine)
        {
            var factories = _enginePlugins?.Where(f => f.Types().Any(t => typeof(T).IsAssignableFrom(t)));
            if (factories != null)
                return factories.Select(f => (T)f.CreateEnginePlugin(engine, typeof(T))).Where(f => f != null);
            return null;
        }

    }
}
