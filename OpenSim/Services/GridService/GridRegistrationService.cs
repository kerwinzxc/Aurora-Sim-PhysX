/*
 * Copyright (c) Contributors, http://aurora-sim.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Aurora-Sim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nini.Config;
using Aurora.Simulation.Base;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Aurora.Framework;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using log4net;

namespace OpenSim.Services.GridService
{
    public class GridRegistrationService : IService, IGridRegistrationService
    {
        #region Declares

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        protected Dictionary<string, IGridRegistrationUrlModule> m_modules = new Dictionary<string, IGridRegistrationUrlModule>();
        protected Dictionary<string, ThreatLevel> m_cachedThreatLevels = new Dictionary<string, ThreatLevel>();
        protected LoadBalancerUrls m_loadBalancer = new LoadBalancerUrls();
        protected IGenericsConnector m_genericsConnector;
        protected ISimulationBase m_simulationBase;
        protected IConfig m_permissionConfig;
        protected IConfig m_configurationConfig;
        protected IRegistryCore m_registry;
        protected bool m_useSessionTime = true;
        protected bool m_useRegistrationService = true;
        /// <summary>
        /// Timeout before the handlers expire (in hours)
        /// </summary>
        protected float m_timeBeforeTimeout = 24;
        public float ExpiresTime 
        { 
            get
            {
                return m_timeBeforeTimeout; 
            }
        }

        protected ThreatLevel m_defaultRegionThreatLevel = ThreatLevel.Full;
        
        protected class PermissionSet
        {
            private static Dictionary<string, ThreatLevel> PermittedFunctions = new Dictionary<string, ThreatLevel>();
            
            public static void ReadFunctions(IConfig config)
            {
                //Combine all threat level configs for ones that are less than our given threat level as well
                foreach (ThreatLevel allThreatLevel in Enum.GetValues(typeof(ThreatLevel)))
                {
                    string list = config.GetString("Threat_Level_" + allThreatLevel.ToString(), "");
                    if (list != "")
                    {
                        string[] functions = list.Split(',');
                        foreach (string function in functions)
                        {
                            string f = function;
                            //Clean them up
                            f = f.Replace(" ", "");
                            f = f.Replace("\r", "");
                            f = f.Replace("\n", "");
                            PermittedFunctions[f] = allThreatLevel;
                        }
                    }
                }
            }

            public static ThreatLevel FindThreatLevelForFunction(string function, ThreatLevel requestedLevel)
            {
                if (PermittedFunctions.ContainsKey(function))
                {
                    return PermittedFunctions[function];
                }
                return requestedLevel;
            }
        }

        #endregion

        #region IService Members

        public void Initialize(IConfigSource config, IRegistryCore registry)
        {
            registry.RegisterModuleInterface<IGridRegistrationService>(this);
            m_registry = registry;
            m_simulationBase = registry.RequestModuleInterface<ISimulationBase>();
            m_simulationBase.EventManager.RegisterEventHandler("GridRegionSuccessfullyRegistered", EventManager_OnGenericEvent);

            m_configurationConfig = config.Configs["Configuration"];
            m_loadBalancer.SetConfig (m_configurationConfig, this);

            if (m_configurationConfig != null)
                m_useSessionTime = m_configurationConfig.GetBoolean ("UseSessionTime", m_useSessionTime);
            if (m_configurationConfig != null)
                m_useRegistrationService = m_configurationConfig.GetBoolean ("UseRegistrationService", m_useRegistrationService);
            m_permissionConfig = config.Configs["RegionPermissions"];
            if (m_permissionConfig != null)
                ReadConfiguration(m_permissionConfig);
        }

        object EventManager_OnGenericEvent (string FunctionName, object parameters)
        {
            if (FunctionName == "GridRegionSuccessfullyRegistered")
            {
                object[] param = (object[])parameters;
                OSDMap resultMap = (OSDMap)param[0];
                UUID SecureSessionID = (UUID)param[1];
                GridRegion rinfo = (GridRegion)param[2];
                OSDMap urls = GetUrlForRegisteringClient (rinfo.RegionHandle.ToString());
                resultMap["URLs"] = urls;
                resultMap["TimeBeforeReRegister"] = m_registry.RequestModuleInterface<IGridRegistrationService> ().ExpiresTime;
                param[0] = resultMap;
                parameters = param;
            }

            return null;
        }

        public void Start(IConfigSource config, IRegistryCore registry)
        {
        }

        public void FinishedStartup()
        {
            IAsyncMessageRecievedService service = m_registry.RequestModuleInterface<IAsyncMessageRecievedService> ();
            if(service != null)
                service.OnMessageReceived += OnMessageReceived;
            m_genericsConnector = Aurora.DataManager.DataManager.RequestPlugin<IGenericsConnector> ();
            LoadFromDatabase ();
        }
        
        /// <summary>
        /// We do handle the RegisterHandlers message here, as we deal with all of the handlers in this module
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        private OSDMap OnMessageReceived(OSDMap message)
        {
            if (!m_useRegistrationService)
                return null;

            if (message.ContainsKey("Method") && message["Method"].AsString() == "RegisterHandlers")
            {
                string SessionID = message["SessionID"];
                if (CheckThreatLevel (SessionID, "RegisterHandlers", ThreatLevel.None))
                {
                    UpdateUrlsForClient (SessionID);
                    OSDMap resp = new OSDMap ();
                    resp["Reregistered"] = true;//It was successful
                    return resp;
                }
            }
            return null;
        }

        protected void ReadConfiguration(IConfig config)
        {
            PermissionSet.ReadFunctions(config);
            m_timeBeforeTimeout = config.GetFloat("DefaultTimeout", m_timeBeforeTimeout);
            m_defaultRegionThreatLevel = (ThreatLevel)Enum.Parse(typeof(ThreatLevel), config.GetString("DefaultRegionThreatLevel", m_defaultRegionThreatLevel.ToString()));
        }

        private ThreatLevel FindRegionThreatLevel(string SessionID)
        {
            ThreatLevel regionThreatLevel = m_defaultRegionThreatLevel;
            if (m_cachedThreatLevels.TryGetValue (SessionID, out regionThreatLevel))
                return regionThreatLevel;
            regionThreatLevel = m_defaultRegionThreatLevel;
            int x, y;
            ulong handle;
            if (ulong.TryParse (SessionID, out handle))
            {
                Util.UlongToInts (handle, out x, out y);
                GridRegion region = m_registry.RequestModuleInterface<IGridService> ().GetRegionByPosition (UUID.Zero, x, y);
                if (region == null)
                    regionThreatLevel = ThreatLevel.None;
                else
                {
                    string rThreat = region.GenericMap["ThreatLevel"].AsString ();
                    if (rThreat != "")
                        regionThreatLevel = (ThreatLevel)Enum.Parse (typeof (ThreatLevel), rThreat);
                }
            }
            m_cachedThreatLevels[SessionID] = regionThreatLevel;
            return regionThreatLevel;
        }

        protected void LoadFromDatabase()
        {
            if (!m_useRegistrationService)
                return;

            List<GridRegistrationURLs> urls = m_genericsConnector.GetGenerics<GridRegistrationURLs>(
                UUID.Zero, "GridRegistrationUrls", new GridRegistrationURLs());

            foreach (GridRegistrationURLs url in urls)
            {
                ulong e;
                if (!ulong.TryParse (url.SessionID, out e))
                {
                    //Don't load links (yet)
                    continue;
                }
                if(url.HostNames == null || url.Ports == null || url.URLS == null)
                {
                    RemoveUrlsForClient(url.SessionID.ToString());
                }
                else
                {
                    foreach (IGridRegistrationUrlModule module in m_modules.Values)
                    {
                        if(url.URLS.ContainsKey(module.UrlName))//Make sure it exists
                            module.AddExistingUrlForClient (url.SessionID.ToString (), url.URLS[module.UrlName], url.Ports[module.UrlName]);
                    }
                    if (m_useSessionTime && (url.Expiration.AddMinutes((m_timeBeforeTimeout * 60) * 0.9)) < DateTime.UtcNow) //Check to see whether the expiration is soon before updating
                    {
                        //Fix the expiration time
                        InnerUpdateUrlsForClient (url);
                    }
                }
            }
        }

        #endregion

        #region IGridRegistrationService Members

        public OSDMap GetUrlForRegisteringClient(string SessionID)
        {
            GridRegistrationURLs urls = m_genericsConnector.GetGeneric<GridRegistrationURLs>(UUID.Zero,
                "GridRegistrationUrls", SessionID.ToString (), new GridRegistrationURLs ());
            OSDMap retVal = new OSDMap();
            if (urls != null)
            {
                if(urls.HostNames == null || urls.Ports == null ||
                    urls.URLS == null || urls.SessionID != SessionID ||
                    !CheckModuleNames(urls))
                {
                    m_log.Warn ("[GridRegService]: Null stuff in GetUrls, HostNames " + (urls.HostNames == null) + ", Ports " +
                        (urls.Ports == null) + ", URLS " + (urls.URLS == null) + ", SessionID 1 " + SessionID + ", SessionID 2 " + urls.SessionID +
                        ", checkModuleNames: " + CheckModuleNames (urls));
                    RemoveUrlsForClient(urls.SessionID);
                }
                else
                {
                    urls.Expiration = DateTime.UtcNow.AddMinutes (m_timeBeforeTimeout * 60);
                    urls.SessionID = SessionID;
                    InnerUpdateUrlsForClient (urls);
                    foreach (KeyValuePair<string, OSD> module in urls.URLS)
                    {
                        //Build the URL
                        retVal[module.Key] = urls.HostNames[module.Key] + ":" + urls.Ports[module.Key] + module.Value.AsString ();
                    }
                    return retVal;
                }
            }
            OSDMap databaseSave = new OSDMap();
            OSDMap ports = new OSDMap();
            OSDMap hostnames = new OSDMap();
            //Get the URLs from all the modules that have registered with us
            foreach (IGridRegistrationUrlModule module in m_modules.Values)
            {
                uint port;
                string hostName;
                string innerURL;

                m_loadBalancer.GetHost (module.UrlName, module, SessionID, out port, out hostName, out innerURL);
                
                ports[module.UrlName] = port;
                hostnames[module.UrlName] = hostName;
                databaseSave[module.UrlName] = innerURL;
            }
            foreach (KeyValuePair<string, OSD> module in databaseSave)
            {
                //Build the URL
                retVal[module.Key] = hostnames[module.Key] + ":" + ports[module.Key] + module.Value.AsString ();
            }

            //Save into the database so that we can rebuild later if the server goes offline
            urls = new GridRegistrationURLs();
            urls.URLS = databaseSave;
            urls.SessionID = SessionID;
            urls.Ports = ports;
            urls.HostNames = hostnames;
            urls.Expiration = DateTime.UtcNow.AddMinutes (m_timeBeforeTimeout * 60);
            m_genericsConnector.AddGeneric (UUID.Zero, "GridRegistrationUrls", SessionID.ToString (), urls.ToOSD ());

            return retVal;
        }

        private bool CheckModuleNames (GridRegistrationURLs urls)
        {
            foreach (string urlName in m_modules.Keys)
            {
                bool found = false;
                foreach (string o in urls.URLS.Keys)
                {
                    if (o == urlName)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    return false;
            }
            return true;
        }

        public void RemoveUrlsForClient(string SessionID)
        {
            if (!m_useRegistrationService)
                return;

            GridRegistrationURLs urls = m_genericsConnector.GetGeneric<GridRegistrationURLs>(UUID.Zero,
                "GridRegistrationUrls", SessionID.ToString (), new GridRegistrationURLs ());
            if (urls != null)
            {
                m_log.WarnFormat ("[GridRegService]: Removing URLs for {0}", SessionID);
                //Remove all the handlers from the HTTP Server
                foreach (IGridRegistrationUrlModule module in m_modules.Values)
                {
                    if (!urls.URLS.ContainsKey(module.UrlName))
                        continue;
                    try
                    {
                        module.RemoveUrlForClient (urls.SessionID, urls.URLS[module.UrlName], urls.Ports[module.UrlName]);
                    }
                    catch
                    {
                    }
                }
                //Remove from the database so that they don't pop up later
                m_genericsConnector.RemoveGeneric (UUID.Zero, "GridRegistrationUrls", SessionID.ToString ());
            }
        }

        public void UpdateUrlsForClient(string SessionID)
        {
            if (!m_useRegistrationService)
                return;

            GridRegistrationURLs urls = m_genericsConnector.GetGeneric<GridRegistrationURLs>(UUID.Zero,
                "GridRegistrationUrls", SessionID.ToString (), new GridRegistrationURLs ());
            InnerUpdateUrlsForClient(urls);
        }

        private void InnerUpdateUrlsForClient(GridRegistrationURLs urls)
        {
            if (urls != null)
            {
                urls.Expiration = DateTime.UtcNow.AddMinutes (m_timeBeforeTimeout * 60);
                //Remove it first just to make sure it is replaced
                m_genericsConnector.RemoveGeneric (UUID.Zero, "GridRegistrationUrls", urls.SessionID.ToString ());
                m_genericsConnector.AddGeneric (UUID.Zero, "GridRegistrationUrls", urls.SessionID.ToString (), urls.ToOSD ());
                m_log.WarnFormat ("[GridRegistrationService]: Updated URLs for {0}", urls.SessionID);
            }
            else
                m_log.ErrorFormat ("[GridRegistrationService]: Failed to find URLs to update for {0}", urls.SessionID);
        }

        public void RegisterModule(IGridRegistrationUrlModule module)
        {
            //Add the module to our list
            m_modules.Add(module.UrlName, module);
        }

        public bool CheckThreatLevel(string SessionID, string function, ThreatLevel defaultThreatLevel)
        {
            if (!m_useRegistrationService)
                return true;

            GridRegistrationURLs urls = m_genericsConnector.GetGeneric<GridRegistrationURLs>(UUID.Zero,
                "GridRegistrationUrls", SessionID.ToString (), new GridRegistrationURLs ());
            if (urls != null)
            {
                //Past time for it to expire
                if (m_useSessionTime && urls.Expiration < DateTime.UtcNow)
                {
                    m_log.Warn ("[GridRegService]: URLs expired for " + SessionID);
                    RemoveUrlsForClient(SessionID);
                    return false;
                }
                //First find the threat level that this setting has to have do be able to run
                ThreatLevel functionThreatLevel = PermissionSet.FindThreatLevelForFunction(function, defaultThreatLevel);
                //Now find the permission for that threat level
                //else, check it against the threat level that the region has
                ThreatLevel regionThreatLevel = FindRegionThreatLevel (SessionID);
                //Return whether the region threat level is higher than the function threat level
                if(!(functionThreatLevel <= regionThreatLevel))
                    m_log.Warn ("[GridRegService]: checkThreatLevel failed for " + SessionID + ", fperm " + functionThreatLevel + ", rperm " + regionThreatLevel + "!");
                return functionThreatLevel <= regionThreatLevel;
            }
            m_log.Warn ("[GridRegService]: Could not find URLs for checkThreatLevel for " + SessionID + "!");
            return false;
        }

        #endregion

        #region Classes

        public class GridRegistrationURLs : IDataTransferable
        {
            public OSDMap URLS;
            public string SessionID;
            public DateTime Expiration;
            public OSDMap HostNames;
            public OSDMap Ports;

            public override OSDMap ToOSD()
            {
                OSDMap retVal = new OSDMap();
                retVal["URLS"] = URLS;
                retVal["SessionID"] = SessionID;
                retVal["Expiration"] = Expiration;
                retVal["HostName"] = HostNames;
                retVal["Port"] = Ports;
                return retVal;
            }

            public override void FromOSD(OSDMap retVal)
            {
                URLS = (OSDMap)retVal["URLS"];
                SessionID = retVal["SessionID"].AsString();
                Expiration = retVal["Expiration"].AsDate ();
                Expiration = Expiration.ToUniversalTime ();
                HostNames = retVal["HostName"] as OSDMap;
                Ports = retVal["Port"] as OSDMap;
            }

            public override IDataTransferable Duplicate()
            {
                GridRegistrationURLs url = new GridRegistrationURLs();
                url.FromOSD(ToOSD());
                return url;
            }
        }

        public class LoadBalancerUrls
        {
            protected Dictionary<string, List<string>> m_urls = new Dictionary<string, List<string>> ();
            protected Dictionary<string, List<uint>> m_ports = new Dictionary<string, List<uint>> ();
            protected Dictionary<string, int> lastSet = new Dictionary<string, int> ();
            protected const uint m_defaultPort = 8003;
            protected string m_defaultHostname = "http://127.0.0.1";
            protected IConfig m_configurationConfig;

            protected int m_externalUrlCountTotal = 0;
            protected List<int> m_externalUrlCount = new List<int> ();
            protected uint m_remotePort = 8003;
            protected List<string> m_remoteLoadBalancingInstances = new List<string> ();
            protected string m_remotePassword = "";

            public void SetConfig (IConfig config, GridRegistrationService gridRegService)
            {
                m_configurationConfig = config;

                if (m_configurationConfig != null)
                {
                    m_defaultHostname = m_configurationConfig.GetString ("HostName", m_defaultHostname);
                    m_remotePassword = m_configurationConfig.GetString ("RemotePassword", "");
                    m_remotePort = m_configurationConfig.GetUInt ("RemoteLoadBalancingPort", m_defaultPort);
                    SetRemoteUrls (m_configurationConfig.GetString ("RemoteLoadBalancingUrls", "").Split (new string[1] { "," }, StringSplitOptions.RemoveEmptyEntries));
                    if (m_configurationConfig.GetBoolean("UseRemoteLoadBalancing", false))
                    {
                        //Set up the external handlers
                        IHttpServer server = gridRegService.m_registry.RequestModuleInterface<ISimulationBase> ().GetHttpServer (m_remotePort);

                        RemoteLoadBalancingPostHandler handler = new RemoteLoadBalancingPostHandler ("/LoadBalancing", m_remotePassword, gridRegService);
                        server.AddStreamHandler (handler);
                    }
                }
            }

            #region Set accessors

            protected void SetRemoteUrls (string[] urls)
            {
                for (int i = 0; i < urls.Length; i++)
                {
                    if (urls[i].StartsWith (" "))
                        urls[i] = urls[i].Remove (0, 1);
                    urls[i] = urls[i].Replace ("http://", "");
                    //Readd the http://
                    urls[i] = "http://" + urls[i];
                }
                m_remoteLoadBalancingInstances = new List<string> (urls);
            }

            protected void SetUrls (string name, string[] urls)
            {
                for (int i = 0; i < urls.Length; i++)
                {
                    if (urls[i].StartsWith (" "))
                        urls[i] = urls[i].Remove (0, 1);
                    //Remove any ports people may have added
                    urls[i] = urls[i].Replace ("http://", "");
                    urls[i] = urls[i].Split (':')[0];
                    //Readd the http://
                    urls[i] = "http://" + urls[i];
                }
                m_urls[name] = new List<string> (urls);
            }

            protected void AddPorts (string name, string[] ports)
            {
                List<uint> uPorts = new List<uint> ();
                for (int i = 0; i < ports.Length; i++)
                {
                    if (ports[i].StartsWith (" "))
                        ports[i] = ports[i].Remove (0, 1);
                    uPorts.Add (uint.Parse (ports[i]));
                }
                if (!m_ports.ContainsKey (name))
                    m_ports[name] = new List<uint> ();
                m_ports[name].AddRange (uPorts);
            }

            #endregion

            #region Get accessors

            /// <summary>
            /// Gets a host and port for the given handler
            /// </summary>
            /// <param name="name"></param>
            /// <param name="port"></param>
            /// <param name="hostName"></param>
            /// <returns>Whether we need to create a handler or whether it is an external URL</returns>
            public void GetHost (string name, IGridRegistrationUrlModule module, string SessionID, out uint port, out string hostName, out string innerUrl)
            {
                if (!m_urls.ContainsKey (name))
                {
                    SetUrls (name, m_configurationConfig.GetString (name + "Hostnames", m_defaultHostname).Split (','));
                    AddPorts (name, m_configurationConfig.GetString (name + "InternalPorts", m_defaultPort.ToString ()).Split (','));
                    GetExternalCounts (name);
                }
                if (!lastSet.ContainsKey (name))
                    lastSet.Add (name, 0);

                //Add both internal and external hosts together for now
                List<string> urls = m_urls[name];

                if (lastSet[name] < urls.Count + m_externalUrlCountTotal)
                {
                    if (lastSet[name] < urls.Count)
                    {
                        //Internal, just pull it from the lists
                        hostName = urls[lastSet[name]];
                        port = m_ports[name][lastSet[name]];
                        innerUrl = module.GetUrlForRegisteringClient (SessionID, port);
                    }
                    else
                    {
                        //Get the external Info
                        if (!GetExternalInfo (lastSet[name], name, SessionID, out port, out hostName, out innerUrl))
                        {
                            lastSet[name] = 0;//It went through all external, give up on them
                            GetHost (name, module, SessionID, out port, out hostName, out innerUrl);
                            return;
                        }
                    }
                    lastSet[name]++;
                    if (lastSet[name] == (urls.Count + m_externalUrlCountTotal))
                        lastSet[name] = 0;
                }
                else
                {
                    //We don't have any urls for this name, return defaults
                    if (m_ports[name].Count > 0)
                    {
                        port = m_ports[name][lastSet[name]];
                        lastSet[name]++;
                        if (lastSet[name] == urls.Count)
                            lastSet[name] = 0;

                        hostName = m_defaultHostname;
                    }
                    else
                    {
                        port = m_defaultPort;
                        hostName = m_defaultHostname;
                    }
                    innerUrl = module.GetUrlForRegisteringClient (SessionID, port);
                }
            }

            private bool GetExternalInfo (int lastSet, string name, string SessionID, out uint port, out string hostName, out string innerUrl)
            {
                port = 0;
                hostName = "";
                innerUrl = "";
                string externalURL = "";
                int currentCount = m_urls.Count;//Start at the end of the urls
                int i = 0;
                for (i = 0; i < m_remoteLoadBalancingInstances.Count; i++)
                {
                    if (currentCount + m_externalUrlCount[i] > lastSet)
                    {
                        externalURL = m_remoteLoadBalancingInstances[i];
                        break;
                    }
                    currentCount += m_externalUrlCount[i];
                }
                if (externalURL == "")
                    return false;

                OSDMap resp = MakeGenericCall (externalURL, "GetExternalInfo", name, SessionID);
                if (resp == null)
                    //Try again
                    return GetExternalInfo ((currentCount + m_externalUrlCount[i]), SessionID, name, out port, out hostName, out innerUrl);
                else
                {
                    port = resp["Port"];
                    hostName = resp["HostName"];
                    innerUrl = resp["InnerUrl"];
                    this.lastSet[name] = lastSet;//Fix this if it has changed
                }
                return true;
            }

            /// <summary>
            /// Gets hostnames and ports from an external instance
            /// </summary>
            /// <param name="name"></param>
            private void GetExternalCounts (string name)
            {
                List<string> urls = new List<string> ();
                int count = 0;
                foreach (string url in m_remoteLoadBalancingInstances)
                {
                    OSDMap resp = MakeGenericCall (url, "GetExternalCounts", name, "");
                    if (resp != null)
                    {
                        m_externalUrlCountTotal += resp["Count"];
                        m_externalUrlCount[count] = resp["Count"];
                    }
                    else
                        m_externalUrlCount[count] = 0;
                    count++;
                }
            }

            private OSDMap MakeGenericCall (string url, string method, string param, string param2)
            {
                OSDMap request = new OSDMap ();
                request["Password"] = m_remotePassword;
                request["Method"] = method;
                request["Param"] = param;
                request["Param2"] = param2;
                return WebUtils.PostToService (url + "/LoadBalancing", request, true, false, true);
            }

            #endregion

            #region Remote Handlers

            public class RemoteLoadBalancingPostHandler : BaseStreamHandler
            {
                private static readonly ILog m_log = LogManager.GetLogger (MethodBase.GetCurrentMethod ().DeclaringType);

                private GridRegistrationService m_service;
                private string m_password;

                public RemoteLoadBalancingPostHandler (string url, string password, GridRegistrationService gridReg) :
                    base ("POST", url)
                {
                    m_password = password;
                    m_service = gridReg;
                }

                public override byte[] Handle (string path, Stream requestData,
                        OSHttpRequest httpRequest, OSHttpResponse httpResponse)
                {
                    StreamReader sr = new StreamReader (requestData);
                    string body = sr.ReadToEnd ();
                    sr.Close ();
                    body = body.Trim ();
                    OSDMap request = WebUtils.GetOSDMap (body);
                    if(request["Password"] != m_password)
                        return null;
                    OSDMap response = new OSDMap ();

                    switch (response["Method"].AsString())
                    {
                        case "GetExternalCounts":
                            response["Count"] = m_service.m_loadBalancer.m_urls[request["Param"]].Count;
                            break;
                        case "GetExternalInfo":
                            string moduleName = request["Param"];
                            string SessionID = request["Param2"];
                            uint port;
                            string hostName, innerUrl;
                            if (m_service.m_modules.ContainsKey (moduleName))
                            {
                                m_service.m_loadBalancer.GetHost (moduleName, m_service.m_modules[moduleName], SessionID, out port, out hostName, out innerUrl);
                                response["HostName"] = hostName;
                                response["InnerUrl"] = innerUrl;
                                response["Port"] = port;
                            }
                            break;
                        default:
                            break;
                    }

                    return Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString (response));
                }
            }

            #endregion
        }

        #endregion
    }
}
