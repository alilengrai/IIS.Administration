// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.


namespace Microsoft.IIS.Administration.WebServer.AppPools
{
    using AspNetCore.Mvc;
    using Core;
    using Core.Utils;
    using System.Linq;
    using System.Net;
    using System.Runtime.InteropServices;
    using System.Threading;
    using Web.Administration;
    using Core.Http;
    using System;

    public class AppPoolsController : ApiBaseController {
        private const string HIDDEN_FIELDS = "model.identity.password";

        [HttpGet]
        [ResourceInfo(Name = Defines.AppPoolsName)]
        public object Get() {
            Fields fields = Context.Request.GetFields();

            // Get reference models for app pool collection
            var pools = ManagementUnit.ServerManager.ApplicationPools.Select(pool => 
                            AppPoolHelper.ToJsonModelRef(pool, fields));

            // Set HTTP header for total count
            this.Context.Response.SetItemsCount(pools.Count());

            // Return the app pool reference model collection
            return new {
                app_pools = pools
            };
        }

        [HttpGet]
        [ResourceInfo(Name = Defines.AppPoolName)]
        public object Get(string id) {
            // Extract the name of the target app pool from the uuid specified in the request
            string name = AppPoolId.CreateFromUuid(id).Name;

            ApplicationPool pool = AppPoolHelper.GetAppPool(name);

            if (pool == null) {
                return NotFound();
            }

            return AppPoolHelper.ToJsonModel(pool, Context.Request.GetFields());
        }

        [HttpPost]
        [Audit(AuditAttribute.ALL, HIDDEN_FIELDS)]
        [ResourceInfo(Name = Defines.AppPoolName)]
        public object Post([FromBody]dynamic model)
        {
            // Create AppPool
            ApplicationPool pool = AppPoolHelper.CreateAppPool(model);

            EnsureAppPoolIdentityAllowed(pool);

            // Save it
            ManagementUnit.ServerManager.ApplicationPools.Add(pool);
            ManagementUnit.Current.Commit();

            // Refresh
            pool = AppPoolHelper.GetAppPool(pool.Name);

            WaitForPoolStatusResolve(pool);

            //
            // Create response
            dynamic appPool = (dynamic) AppPoolHelper.ToJsonModel(pool, Context.Request.GetFields());

            // A newly created application should default to started state
            if (pool.State == ObjectState.Unknown) {
                appPool.status = Enum.GetName(typeof(Status), Status.Started).ToLower();
            }

            return Created((string)AppPoolHelper.GetLocation(appPool.id), appPool);
        }

        [HttpDelete]
        [Audit]
        public void Delete(string id)
        {
            string name = AppPoolId.CreateFromUuid(id).Name;

            ApplicationPool pool = AppPoolHelper.GetAppPool(name);

            if (pool != null) {
                AppPoolHelper.DeleteAppPool(pool);
                ManagementUnit.Current.Commit();
            }

            // Sucess
            Context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        }


        [HttpPatch]
        [Audit(AuditAttribute.ALL, HIDDEN_FIELDS)]
        [ResourceInfo(Name = Defines.AppPoolName)]
        public object Patch(string id, [FromBody] dynamic model)
        {
            // Cut off the notion of uuid from beginning of request
            string name = AppPoolId.CreateFromUuid(id).Name;

            // Set settings
            ApplicationPool appPool = AppPoolHelper.UpdateAppPool(name, model);
            if(appPool == null) {
                return NotFound();
            }

            if (model.identity != null) {
                EnsureAppPoolIdentityAllowed(appPool);
            }

            // Start/Stop
            if (model.status != null) {
                Status status = DynamicHelper.To<Status>(model.status);
                try {
                    switch (status) {
                        case Status.Stopped:
                            appPool.Stop();
                            break;
                        case Status.Started:
                            appPool.Start();
                            break;
                    }
                }
                catch(COMException e) {

                    // If pool is fresh and status is still unknown then COMException will be thrown when manipulating status
                    throw new ApiException("Error setting application pool status", e);
                }
            }

            // Update changes
            ManagementUnit.Current.Commit();

            // Refresh data
            appPool = ManagementUnit.ServerManager.ApplicationPools[appPool.Name];

            //
            // Create response
            dynamic pool = AppPoolHelper.ToJsonModel(appPool, Context.Request.GetFields());

            // The Id could change by changing apppool name
            if (pool.id != id) {
                return LocationChanged(AppPoolHelper.GetLocation(pool.id), pool);
            }

            return pool;
        }


        private void WaitForPoolStatusResolve(ApplicationPool pool)
        {
            // Delay to get proper status of newly created pool
            int n = 10;
            for (int i = 0; i < n; i++) {
                try {
                    StatusExtensions.FromObjectState(pool.State);
                    break;
                }
                catch (COMException) {
                    if (i < n - 1) {
                        Thread.Sleep(10 / n);
                    }
                }
            }
        }

        private void EnsureAppPoolIdentityAllowed(ApplicationPool pool) {
            if (pool.ProcessModel.IdentityType != ProcessModelIdentityType.LocalSystem) {
                return;
            }

            //
            // Only admins can set up LocalSystem AppPool identity

            if (!User.IsInRole("Administrators")) {
                throw new UnauthorizedArgumentException("identity.identity_type");
            }
        }
    }
}
