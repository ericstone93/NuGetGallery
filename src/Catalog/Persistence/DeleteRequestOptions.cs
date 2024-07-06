﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Azure;
using Azure.Storage.Blobs.Models;
using NuGetGallery;
using System;

namespace NuGet.Services.Metadata.Catalog.Persistence
{
    public abstract class DeleteRequestOptions
    {
    }

    /// <summary>
    /// Allows specifying an <see cref="AccessCondition"/> for use by <see cref="AzureStorage"/> in a <see cref="Storage.DeleteAsync(Uri, System.Threading.CancellationToken, DeleteRequestOptions)"/> operation.
    /// </summary>
    public class DeleteRequestOptionsWithAccessCondition : DeleteRequestOptions
    {
        private readonly Lazy<BlobRequestConditions> _lazyBlobRequestConditions;

        public DeleteRequestOptionsWithAccessCondition(IAccessCondition accessCondition)
        {
            AccessCondition = accessCondition ?? throw new ArgumentNullException(nameof(accessCondition));
            _lazyBlobRequestConditions = new Lazy<BlobRequestConditions>(CreateBlobRequestConditions);
        }

        public IAccessCondition AccessCondition { get; }

        public BlobRequestConditions BlobRequestConditions => _lazyBlobRequestConditions.Value;

        private BlobRequestConditions CreateBlobRequestConditions()
        {
            return new BlobRequestConditions
            {
                IfMatch = new ETag(AccessCondition.IfMatchETag),
                IfNoneMatch = new ETag(AccessCondition.IfNoneMatchETag)
            };
        }
    }
}
