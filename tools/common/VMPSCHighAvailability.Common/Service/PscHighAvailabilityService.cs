﻿/*
 * Copyright © 2012-2015 VMware, Inc.  All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the “License”); you may not
 * use this file except in compliance with the License.  You may obtain a copy
 * of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an “AS IS” BASIS, without
 * warranties or conditions of any kind, EITHER EXPRESS OR IMPLIED.  See the
 * License for the specific language governing permissions and limitations
 * under the License.
 */

using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml;
using VMAFD.Client;
using VMPSCHighAvailability.Common.DTO;
using VMPSCHighAvailability.Common.Helpers;
using vmdirClient = VMDIR.Client;
using VMIdentity.CommonUtils;
using System.Collections.Concurrent;
using VMIdentity.CommonUtils.Utilities;
using VMDirInterop.LDAP;
using VMDirInterop.Interfaces;

namespace VMPSCHighAvailability.Common.Service
{
    public class PscHighAvailabilityService : IPscHighAvailabilityService
    {
        /// <summary>
        /// Gets the infrastructure nodes.
        /// </summary>
        /// <returns>The infrastructure nodes.</returns>
        /// <param name="serverDto">Server dto.</param>
        /// <param name="dcName">Dc name.</param>
        private List<NodeDto> GetInfrastructureNodes(ServerDto serverDto, string dcName)
        {
            var nodes = new List<NodeDto>();

            // Get Infrastructure nodes
            var entries = vmdirClient.Client.VmDirGetDCInfos(dcName, serverDto.UserName, serverDto.Password);

            foreach (vmdirClient.VmDirDCInfo entry in entries)
            {
                var infraNode = new InfrastructureDto()
                {
                    Name = entry.pszDCName,
                    Sitename = entry.pszSiteName,
                    Partners = entry.partners, 
                    NodeType = NodeType.Infrastructure,
                    Ip = Network.GetIpAddress(entry.pszDCName)
                };
                nodes.Add(infraNode);
            }
            return nodes;
        }

        /// <summary>
        /// Gets the management nodes.
        /// </summary>
        /// <returns>The management nodes.</returns>
        /// <param name="serverDto">Server dto.</param>
        /// <param name="dcName">Dc name.</param>
        /// <param name="siteName">Site name.</param>
        private List<NodeDto> GetManagementNodes(ServerDto serverDto, string dcName, string site)
        {
            var nodes = new ConcurrentBag<NodeDto>();

            // Get Management nodes
            var mgmtNodes = vmdirClient.Client.VmDirGetComputers(dcName, serverDto.UserName, serverDto.Password);
            
            if (mgmtNodes != null && mgmtNodes.Count > 0)
            {
                var tasks = new Task[mgmtNodes.Count];
                var index = 0;
                // Update management nodes with site name
                foreach (var mgmt in mgmtNodes)
                {
                    tasks[index++] = Task.Factory.StartNew(() =>
                    {
                        var siteName = string.Empty;
                        var s = new ServerDto
                        {
                            Server = mgmt,
                            Upn = serverDto.Upn,
                            Password = serverDto.Password,
                            UserName = serverDto.UserName
                        };
                        //var client = CanConnect(s);
                        using (Client client = new Client(s.Server, s.Upn, s.Password))
                        {
                            siteName = client.VmAfdGetSiteName();
                        }
                        var mgmtNode = new ManagementDto()
                        {
                            Name = mgmt,
                            Sitename = siteName,
                            NodeType = NodeType.Management,
                            Ip = Network.GetIpAddress(mgmt)
                        };
                        nodes.Add(mgmtNode);

                    });
                }
                Task.WaitAll(tasks);
            }
            return nodes.ToList();
        }

        /// <summary>
        /// Gets the nodes.
        /// </summary>
        /// <returns>The nodes.</returns>
        /// <param name="serverDto">Server dto.</param>
        /// <param name="dcName">Dc name.</param>
        /// <param name="siteName">Site name.</param>
        private List<NodeDto> GetNodes(ServerDto serverDto, string dcName, string siteName)
        {
            List<NodeDto> list1 = null, list2 = null;
            var t1 = Task.Run(() => { list1 = GetInfrastructureNodes(serverDto, dcName); });
            var t2 = Task.Run(() => { list2 = GetManagementNodes(serverDto, dcName, siteName); });
            var tasks = new Task[] { t1, t2 };
            var result = Task.WaitAll(tasks, Constants.TopologyTimeout * Constants.MilliSecsMultiplier);

            if (!result)
                throw new Exception(Constants.TolologyApiIsTakenLonger);
            else
            {
                if (t1.Exception != null)
                    throw t1.Exception;
                if (t2.Exception != null)
                    throw t2.Exception;
            }
            if (list2 != null)
            {
                list2.AddRange(list1);
                return list2;
            }
            return list1;
        }

        /// <summary>
        /// Get the topology of the HA setup
        /// </summary>
        /// <returns>List of the nodes</returns>
        public List<NodeDto> GetTopology(ManagementDto dto, ServerDto serverDto)
        {
            var nodes = GetNodes(serverDto, dto.DomainController.Name, dto.Sitename);
            return nodes;
        }

        /// <summary>
        /// Sets the management node to legacy mode
        /// </summary>
        /// <param name="legacy">True if legacy mode needs to be set false otherwise</param>
        /// <param name="serverDto">Management node details</param>
        public void SetLegacyMode(bool legacy, ServerDto serverDto)
        {

            using (Client client = new Client(serverDto.Server, serverDto.Upn, serverDto.Password))
            {
                if (legacy)
                    client.CdcDisableClientAffinity();
                else
                    client.CdcEnableClientAffinity();
            }
        }

        /// <summary>
        /// Adds the service.
        /// </summary>
        /// <param name="infra">Infra.</param>
        /// <param name="service">Service.</param>
        private static void AddService(InfrastructureDto infra, ServiceDto service)
        {
            var index = infra.Services.FindIndex(x => x.ServiceName == service.ServiceName);
            if (index > -1)
            {
                if (index < infra.Services.Count)
                    infra.Services[index] = service;
            }
            else
            {
                infra.Services.Add(service);
            }
        }

        /// <summary>
        /// Updates the PSC dto with the latest infrastructure node details
        /// </summary>
        /// <param name="pscDto">Psc Dto</param>
        /// <param name="serverDto">Infrasturtcture node details</param>
        /// <returns>Updated Psc dto</returns>
        public NodeDto UpdateStatus(NodeDto dto, ServerDto serverDto)
        {
            try
            {
                VMAFD_HEARTBEAT_STATUS status;
                using (Client client = new Client(serverDto.Server, serverDto.Upn, serverDto.Password))
                {
                    status = client.VmAfdGetHeartbeatStatus();
                }
                dto.Active = status.bIsAlive == 1;

                var infraDto = dto as InfrastructureDto;
                if (infraDto != null)
                {
                    var service = GetVmDirServiceStatus(serverDto);
                    infraDto.Services = ServiceHelper.GetServices(dto.Name, status);
                    infraDto.Services.Add(service);
                    if (!infraDto.Services.Exists(x => x.Alive))
                        infraDto.Active = false;
                }
            }
            catch (Exception exception)
            {
                dto.Active = false;
                var infra = (dto as InfrastructureDto);
                if (infra.Services == null)
                    infra.Services = new List<ServiceDto>();
                var afdService = new ServiceDto()
                {
                    HostName = serverDto.Server,
                    ServiceName = Constants.AuthFrameworkServiceName,
                    Description = Constants.AuthFrameworkServiceDesc,
                    Alive = false,
                    LastHeartbeat = System.DateTime.Now
                };
                AddService(infra, afdService);
                var vmdirService = GetVmDirServiceStatus(serverDto);
                AddService(infra, vmdirService);
            }
            return dto;
        }


        private void AddAfdAndDirServiceStatus(ServerDto serverDto, InfrastructureDto infra)
        {
            var afdService = new ServiceDto()
            {
                HostName = serverDto.Server,
                ServiceName = Constants.AuthFrameworkServiceName,
                Description = Constants.AuthFrameworkServiceDesc,
                Alive = false,
                LastHeartbeat = System.DateTime.Now
            };

            AddService(infra, afdService);
            var vmdirService = GetVmDirServiceStatus(serverDto);
            AddService(infra, vmdirService);
        }

        private void AddAfdAndDirServiceStatusWithInActiveStatus(ServerDto serverDto, InfrastructureDto infra)
        {
            var afdService = new ServiceDto()
            {
                HostName = serverDto.Server,
                ServiceName = Constants.AuthFrameworkServiceName,
                Description = Constants.AuthFrameworkServiceDesc,
                Alive = false,
                LastHeartbeat = System.DateTime.Now
            };
            var vmdirService = new ServiceDto
            {
                HostName = serverDto.Server,
                ServiceName = Constants.DirectoryServiceName,
                Description = Constants.DirectoryServiceDesc,
                LastHeartbeat = System.DateTime.Now,
                Alive = false
            };
            AddService(infra, afdService);
            AddService(infra, vmdirService);
        }

        /// <summary>
        /// Gets the vmdir service status.
        /// </summary>
        /// <returns>The infrastructure nodes.</returns>
        /// <param name="serverDto">Server dto.</param>
        private ServiceDto GetVmDirServiceStatus(ServerDto serverDto)
        {
            var dto = new ServiceDto
            {
                HostName = serverDto.Server,
                ServiceName = Constants.DirectoryServiceName,
                Description = Constants.DirectoryServiceDesc,
                LastHeartbeat = System.DateTime.Now
            };

            try
            {
                var entries = vmdirClient.Client.VmDirGetDCInfos(serverDto.Server, serverDto.UserName, serverDto.Password);
                dto.Alive = true;
            }
            catch (Exception exc)
            {
                dto.Alive = false;
            }
            return dto;
        }

        /// <summary>
        /// Checks connectivity of the server
        /// </summary>
        /// <param name="serverDto">Server with credentials i.e. UPN and password</param>
        /// <returns>True on success false otherwise</returns>
        public ManagementDto Connect(ServerDto serverDto)
        {
            var dto = new ManagementDto { Name = serverDto.Server };
            try
            {
                using (Client client = new Client(serverDto.Server, serverDto.Upn, serverDto.Password))
                {

                    dto.Sitename = client.VmAfdGetSiteName();
                    var domainController = client.CdcGetDCName(serverDto.DomainName, dto.Sitename, 0);
                    dto.DomainController = new InfrastructureDto
                    {
                        Name = domainController.pszDCName
                    };
                }
            }
            catch (Exception exc)
            {
                dto = null;
            }
            return dto;
        }

        /// <summary>
        /// Gets the management node details.
        /// </summary>
        /// <returns>The management node details.</returns>
        /// <param name="serverDto">Server dto.</param>
        public ManagementDto GetManagementNodeDetails(ServerDto serverDto)
        {
            var dto = new ManagementDto() { State = new StateDescriptionDto(), Name = serverDto.Server, Domain = serverDto.DomainName };
            using (Client client = new Client(serverDto.Server, serverDto.Upn, serverDto.Password))
            {
                var state = client.CdcGetCurrentState();
                dto.Legacy = (state == CDC_DC_STATE.CDC_DC_STATE_LEGACY);
                dto.State = CdcDcStateHelper.GetStateDescription(state);
                dto.Sitename = client.VmAfdGetSiteName();
                dto.Active = true;
                dto.Ip = Network.GetIpAddress(dto.Name);
                var dcInfo = client.CdcGetDCName(serverDto.DomainName, dto.Sitename, 0);
                dto.DomainController = new InfrastructureDto
                {
                    Name = dcInfo.pszDCName,
                    NodeType = NodeType.Infrastructure,
                    Domain = dcInfo.pszDomainName
                };
                dto.DomainControllers = new List<InfrastructureDto>();

                IList<string> entries = client.CdcEnumDCEntries();
                foreach (var entry in entries)
                {
                    CDC_DC_STATUS_INFO info;
                    VMAFD_HEARTBEAT_STATUS hbStatus;
                    client.CdcGetDCStatus(entry, string.Empty, out info, out hbStatus);

                    var infraDto = new InfrastructureDto()
                    {
                        Name = entry,
                        Active = info.bIsAlive == 1,
                        Sitename = info.pszSiteName,
                        LastPing = DateTimeConverter.FromUnixToLocalDateTime(info.dwLastPing),
                        Services = new List<ServiceDto>()

                    };

                    if (hbStatus.info != null)
                    {
                        foreach (var serviceInfo in hbStatus.info)
                        {
                            var service = new ServiceDto
                            {
                                ServiceName = ServiceHelper.GetServiceName(serviceInfo.pszServiceName),
                                Description = ServiceHelper.GetServiceDescription(serviceInfo.pszServiceName),
                                Alive = serviceInfo.bIsAlive == 1,
                                LastHeartbeat = DateTimeConverter.FromUnixToLocalDateTime(serviceInfo.dwLastHeartbeat),
                                Port = serviceInfo.dwPort,
                            };
                            infraDto.Services.Add(service);
                        }
                    }
                    dto.DomainControllers.Add(infraDto);
                }
            }
            return dto;
        }

        /// <summary>
        /// Gets the domain functional level for the server
        /// </summary>
        /// <param name="serverDto">Server details</param>
        /// <returns>Domain functional level</returns>
        public string GetDomainFunctionalLevel(ServerDto serverDto)
        {
            var dfl = string.Empty;
            var helper = new LdapSearchHelper(serverDto.Server, serverDto.Upn, serverDto.Password);
            var searchDN = "DC=" + serverDto.DomainName.Replace(".", ", DC=");
            Action<ILdapMessage, List<ILdapEntry>> action = delegate(ILdapMessage searchRequest, List<ILdapEntry> values)
            {
                dfl = GetDomainFunctionalLevel(searchRequest, values);
            };
            helper.Search(searchDN, LdapScope.SCOPE_BASE, "(objectClass=*)", new[] { "+" }, true, action);
            return dfl;
        }

        private string GetDomainFunctionalLevel(ILdapMessage searchRequest, List<ILdapEntry> values)
        {
            if (values != null)
            {
                var value = values[0].getAttributeValues("vmwDomainFunctionalLevel");
                if (value != null && value.Count > 0)
                    return value[0].StringValue;
            }
            return string.Empty;
        }
    }
}