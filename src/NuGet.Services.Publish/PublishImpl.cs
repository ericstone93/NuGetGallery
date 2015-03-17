﻿using Microsoft.Owin;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json.Linq;
using NuGet.Services.Metadata.Catalog;
using NuGet.Services.Metadata.Catalog.Ownership;
using NuGet.Services.Metadata.Catalog.Persistence;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;

namespace NuGet.Services.Publish
{
    public abstract class PublishImpl
    {
        const string DefaultStorageContainerCatalog = "catalog";
        const string DefaultStorageContainerPackages = "artifacts";
        const string DefaultStorageContainerOwnership = "ownership";

        static string StoragePrimary;
        static string StorageContainerCatalog;
        static string StorageContainerPackages;
        static string StorageContainerOwnership;
        static string CatalogBaseAddress;

        IRegistrationOwnership _registrationOwnership;

        static PublishImpl()
        {
            StoragePrimary = System.Configuration.ConfigurationManager.AppSettings.Get("Storage.Primary");
            StorageContainerCatalog = System.Configuration.ConfigurationManager.AppSettings.Get("Storage.Container.Catalog") ?? DefaultStorageContainerCatalog;
            StorageContainerPackages = System.Configuration.ConfigurationManager.AppSettings.Get("Storage.Container.Artifacts") ?? DefaultStorageContainerPackages;
            StorageContainerOwnership = System.Configuration.ConfigurationManager.AppSettings.Get("Storage.Container.Ownership") ?? DefaultStorageContainerOwnership;
            CatalogBaseAddress = System.Configuration.ConfigurationManager.AppSettings.Get("Catalog.BaseAddress");
        }

        public PublishImpl(IRegistrationOwnership registrationOwnership)
        {
            _registrationOwnership = registrationOwnership;
        }

        protected abstract bool IsMetadataFile(string fullName);
        protected abstract JObject CreateMetadataObject(string fullname, Stream stream);
        protected abstract Uri GetItemType();
        protected abstract IList<string> Validate(Stream nupkgStream, out PackageIdentity packageIdentity);

        protected virtual void InferArtifactTypes(IDictionary<string, JObject> metadata)
        {
        }

        protected virtual void GenerateNuspec(IDictionary<string, JObject> metadata)
        {
        }

        public async Task Upload(IOwinContext context)
        {
            if (!_registrationOwnership.IsAuthenticated)
            {
                await ServiceHelpers.WriteErrorResponse(context, "user does not have access to the service", HttpStatusCode.Forbidden);
                return;
            }

            if (!await _registrationOwnership.HasTenantEnabled())
            {
                await ServiceHelpers.WriteErrorResponse(context, "package publication has not been enabled in this tenant", HttpStatusCode.Forbidden);
                return;
            }

            PublicationVisibility publicationVisibility;
            if (!PublicationVisibility.TryCreate(context, out publicationVisibility))
            {
                await ServiceHelpers.WriteErrorResponse(context, "specify either organization OR subscription NOT BOTH", HttpStatusCode.BadRequest);
                return;
            }

            Stream packageStream = context.Request.Body;
            PackageIdentity packageIdentity;

            //  validation

            IEnumerable<string> validationErrors = Validate(packageStream, out packageIdentity);

            if (validationErrors != null)
            {
                await ServiceHelpers.WriteErrorResponse(context, validationErrors, HttpStatusCode.BadRequest);
                return;
            }

            //  registration authorization

            IEnumerable<string> authorizationErrors = await CheckRegistrationAuthorization(packageIdentity);

            if (authorizationErrors != null)
            {
                await ServiceHelpers.WriteErrorResponse(context, authorizationErrors, HttpStatusCode.Forbidden);
                return;
            }

            //  process the package

            IDictionary<string, JObject> metadata = new Dictionary<string, JObject>();

            //  (1) save all the artifacts

            await Artifacts.Save(metadata, packageStream, StoragePrimary, StorageContainerPackages);

            InferArtifactTypes(metadata);

            //  (2) promote the relevant peices of metadata so they later can appear on the catalog page 

            ExtractMetadata(metadata, packageStream);

            //  (3) gather all the publication details

            PublicationDetails publicationDetails = await CreatePublicationDetails(publicationVisibility);

            //  (4) add the new item to the catalog

            Uri catalogAddress = await AddToCatalog(metadata["nuspec"], GetItemType(), publicationDetails);

            //  (5) update the registration ownership record

            await UpdateRegistrationOwnership(packageIdentity);

            //  (6) create response

            JToken response = new JObject
            { 
                { "download", metadata["nuspec"]["packageContent"] },
                { "catalog", catalogAddress.ToString() }
            };

            await ServiceHelpers.WriteResponse(context, response, HttpStatusCode.OK);
        }

        async Task<IEnumerable<string>> CheckRegistrationAuthorization(PackageIdentity packageIdentity)
        {
            IList<string> errors = new List<string>();

            if (await _registrationOwnership.HasRegistration(packageIdentity.Namespace, packageIdentity.Id))
            {
                if (!await _registrationOwnership.HasOwner(packageIdentity.Namespace, packageIdentity.Id))
                {
                    errors.Add("user does not have access to this registration");
                    return errors;
                }

                if (await _registrationOwnership.HasVersion(packageIdentity.Namespace, packageIdentity.Id, packageIdentity.Version.ToString()))
                {
                    errors.Add("this package version already exists for this registration");
                    return errors;
                }
            }

            if (errors.Count == 0)
            {
                return null;
            }

            return errors;
        }

        async Task<PublicationDetails> CreatePublicationDetails(PublicationVisibility publicationVisibility)
        {
            string userId = _registrationOwnership.GetUserId();
            string userName = await _registrationOwnership.GetPublisherName();
            string tenantId = _registrationOwnership.GetTenantId();

            //TODO: requires Graph access
            string tenantName = string.Empty;
            //string tenantName = await _registrationOwnership.GetTenantName();

            //var client = await ServiceHelpers.GetActiveDirectoryClient();

            PublicationDetails publicationDetails = new PublicationDetails
            {
                Published = DateTime.UtcNow,
                Owner = OwnershipOwner.Create(ClaimsPrincipal.Current),
                TenantId = tenantId,
                TenantName = tenantName,
                Visibility = publicationVisibility
            };

            return publicationDetails;
        }

        public async Task GetDomains(IOwinContext context)
        {
            if (!_registrationOwnership.IsAuthenticated)
            {
                await ServiceHelpers.WriteErrorResponse(context, "user does not have access to the service", HttpStatusCode.Forbidden);
                return;
            }

            IEnumerable<string> domains = await _registrationOwnership.GetDomains();
            await ServiceHelpers.WriteResponse(context, new JArray(domains.ToArray()), HttpStatusCode.OK);
        }

        public async Task GetTenants(IOwinContext context)
        {
            if (!_registrationOwnership.IsAuthenticated)
            {
                await ServiceHelpers.WriteErrorResponse(context, "user does not have access to the service", HttpStatusCode.Forbidden);
                return;
            }

            IEnumerable<string> tenants = await _registrationOwnership.GetTenants();
            await ServiceHelpers.WriteResponse(context, new JArray(tenants.ToArray()), HttpStatusCode.OK);
        }

        public async Task TenantEnable(IOwinContext context)
        {
            if (!_registrationOwnership.IsAuthenticated)
            {
                await ServiceHelpers.WriteErrorResponse(context, "user does not have access to the service", HttpStatusCode.Forbidden);
                return;
            }

            if (!await _registrationOwnership.IsUserAdministrator())
            {
                await ServiceHelpers.WriteErrorResponse(context, "this operation is only permitted for administrators", HttpStatusCode.Forbidden);
                return;
            }

            await _registrationOwnership.EnableTenant();

            context.Response.StatusCode = (int)HttpStatusCode.OK;
        }

        public async Task TenantDisable(IOwinContext context)
        {
            if (!_registrationOwnership.IsAuthenticated)
            {
                await ServiceHelpers.WriteErrorResponse(context, "user does not have access to the service", HttpStatusCode.Forbidden);
                return;
            }

            if (!await _registrationOwnership.IsUserAdministrator())
            {
                await ServiceHelpers.WriteErrorResponse(context, "this operation is only permitted for administrators", HttpStatusCode.Forbidden);
                return;
            }

            await _registrationOwnership.DisableTenant();

            context.Response.StatusCode = (int)HttpStatusCode.OK;
        }

        void ExtractMetadata(IDictionary<string, JObject> metadata, Stream nupkgStream)
        {
            using (ZipArchive archive = new ZipArchive(nupkgStream, ZipArchiveMode.Read, true))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (IsMetadataFile(entry.FullName))
                    {
                        using (Stream stream = entry.Open())
                        {
                            metadata.Add(entry.FullName, CreateMetadataObject(entry.FullName, stream));
                        }
                    }
                }
            }

            if (!metadata.ContainsKey("nuspec"))
            {
                GenerateNuspec(metadata);
            }
        }

        static async Task<Uri> AddToCatalog(JObject nuspec, Uri itemType, PublicationDetails publicationDetails)
        {
            StorageWriteLock writeLock = new StorageWriteLock(StoragePrimary, StorageContainerCatalog);

            await writeLock.AquireAsync();

            Uri rootUri = null;

            Exception exception = null;
            try
            {
                CloudStorageAccount account = CloudStorageAccount.Parse(StoragePrimary);

                Storage storage;
                if (CatalogBaseAddress == null)
                {
                    storage = new AzureStorage(account, StorageContainerCatalog);
                }
                else
                {
                    string baseAddress = CatalogBaseAddress.TrimEnd('/') + "/" + StorageContainerCatalog;

                    storage = new AzureStorage(account, StorageContainerCatalog, string.Empty, new Uri(baseAddress));
                }

                AppendOnlyCatalogWriter writer = new AppendOnlyCatalogWriter(storage);
                writer.Add(new GraphCatalogItem(nuspec, itemType, publicationDetails));
                await writer.Commit();

                rootUri = writer.RootUri;
            }
            catch (Exception e)
            {
                exception = e;
            }

            await writeLock.ReleaseAsync();

            if (exception != null)
            {
                throw exception;
            }

            return rootUri;
        }

        async Task UpdateRegistrationOwnership(PackageIdentity packageIdentity)
        {
            StorageWriteLock writeLock = new StorageWriteLock(StoragePrimary, StorageContainerOwnership);

            await writeLock.AquireAsync();

            Exception exception = null;
            try
            {
                await _registrationOwnership.AddVersion(packageIdentity.Namespace, packageIdentity.Id, packageIdentity.Version.ToString());
            }
            catch (Exception e)
            {
                exception = e;
            }

            await writeLock.ReleaseAsync();

            if (exception != null)
            {
                throw exception;
            }
        }
    }
}