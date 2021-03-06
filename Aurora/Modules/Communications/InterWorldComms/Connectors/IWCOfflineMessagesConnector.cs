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
using System.Linq;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Nini.Config;
using Aurora.Framework;
using Aurora.Simulation.Base;
using Aurora.Services.DataService;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Services.AvatarService;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace Aurora.Modules 
{
    public class IWCOfflineMessagesConnector : IOfflineMessagesConnector
    {
        protected LocalOfflineMessagesConnector m_localService;
        protected RemoteOfflineMessagesConnector m_remoteService;

        private IRegistryCore m_registry;

        public void Initialize (IGenericData unneeded, IConfigSource source, IRegistryCore simBase, string defaultConnectionString)
        {
            if (source.Configs["AuroraConnectors"].GetString ("OfflineMessagesConnector", "LocalConnector") == "IWCConnector")
            {
                m_localService = new LocalOfflineMessagesConnector ();
                m_localService.Initialize (unneeded, source, simBase, defaultConnectionString);
                m_remoteService = new RemoteOfflineMessagesConnector ();
                m_remoteService.Initialize (unneeded, source, simBase, defaultConnectionString);
                m_registry = simBase;
                DataManager.DataManager.RegisterPlugin (Name, this);
            }
        }

        public string Name
        {
            get { return "IOfflineMessagesConnector"; }
        }

        public void Dispose ()
        {
        }

        #region IOfflineMessagesConnector Members

        public GridInstantMessage[] GetOfflineMessages (UUID agentID)
        {
            List<string> serverURIs = m_registry.RequestModuleInterface<IConfigurationService> ().FindValueOf (agentID.ToString (), "FriendsServerURI");
            if (serverURIs.Count > 0) //Remote user... or should be
                return m_remoteService.GetOfflineMessages (agentID);
            return m_localService.GetOfflineMessages (agentID);
        }

        public bool AddOfflineMessage (GridInstantMessage message)
        {
            List<string> serverURIs = m_registry.RequestModuleInterface<IConfigurationService> ().FindValueOf (message.toAgentID.ToString (), "FriendsServerURI");
            if (serverURIs.Count > 0) //Remote user... or should be
                return m_remoteService.AddOfflineMessage (message);
            return m_localService.AddOfflineMessage (message);
        }

        #endregion
    }
}
