/*
* Copyright (c) Contributors, http://opensimulator.org/
* See CONTRIBUTORS.TXT for a full list of copyright holders.
*
* Redistribution and use in source and binary forms, with or without
* modification, are permitted provided that the following conditions are met:
*     * Redistributions of source code must retain the above copyright
*       notice, this list of conditions and the following disclaimer.
*     * Redistributions in binary form must reproduce the above copyright
*       notice, this list of conditions and the following disclaimer in the
*       documentation and/or other materials provided with the distribution.
*     * Neither the name of the OpenSim Project nor the
*       names of its contributors may be used to endorse or promote products
*       derived from this software without specific prior written permission.
*
* THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS AS IS AND ANY
* EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
* WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
* DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
* DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
* (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
* LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
* ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
* (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
* SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
* 
*/

using System;
using System.Reflection;
using OpenSim.Framework.Console;
using OpenSim.Region.Environment.Interfaces;

namespace OpenSim.Region.Environment
{
    public class StorageManager
    {
        private IRegionDataStore m_dataStore;

        public IRegionDataStore DataStore
        {
            get { return m_dataStore; }
        }

        public StorageManager(IRegionDataStore storage)
        {
            m_dataStore = storage;
        }

        public StorageManager(string dllName, string dataStoreFile, string dataStoreDB)
        {
            MainLog.Instance.Verbose("DATASTORE", "Attempting to load " + dllName);
            Assembly pluginAssembly = Assembly.LoadFrom(dllName);

            foreach (Type pluginType in pluginAssembly.GetTypes())
            {
                if (pluginType.IsPublic)
                {
                    Type typeInterface = pluginType.GetInterface("IRegionDataStore", true);

                    if (typeInterface != null)
                    {
                        IRegionDataStore plug =
                            (IRegionDataStore) Activator.CreateInstance(pluginAssembly.GetType(pluginType.ToString()));
                        plug.Initialise(dataStoreFile, dataStoreDB);

                        m_dataStore = plug;

                        MainLog.Instance.Verbose("DATASTORE", "Added IRegionDataStore Interface");
                    }

                    typeInterface = null;
                }
            }

            pluginAssembly = null;

            //TODO: Add checking and warning to make sure it initialised.
        }
    }
}
