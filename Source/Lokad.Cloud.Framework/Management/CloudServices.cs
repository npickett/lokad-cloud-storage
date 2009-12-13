﻿#region Copyright (c) Lokad 2009
// This code is released under the terms of the new BSD licence.
// URL: http://www.lokad.com/
#endregion

using System.Collections.Generic;
using System.Linq;
using Lokad.Cloud.Services;

// TODO: blobs are sequentially enumerated, performance issue
// if there are more than a few dozen services

namespace Lokad.Cloud.Management
{
	/// <summary>
	/// Management facade for cloud services.
	/// </summary>
	public class CloudServices
	{
		readonly IBlobStorageProvider _blobProvider;

		/// <summary>
		/// Initializes a new instance of the <see cref="CloudServices"/> class.
		/// </summary>
		public CloudServices(IBlobStorageProvider blobStorageProvider)
		{
			_blobProvider = blobStorageProvider;
		}

		/// <summary>
		/// Enumerate infos of all cloud services.
		/// </summary>
		public IEnumerable<ServiceInfo> GetServices()
		{
			foreach (var blobName in _blobProvider.List(CloudServiceStateName.GetPrefix()))
			{
				var blob = _blobProvider.GetBlobOrDelete<CloudServiceState?>(blobName);
				if (!blob.HasValue)
				{
					continue;
				}

				var state = blob.Value;
				yield return new ServiceInfo
					{
						ServiceName = blobName.ServiceName,
						State = state.Value
					};
			}
		}

		/// <summary>
		/// Enumerate the names of all cloud services.
		/// </summary>
		public IEnumerable<string> GetServiceNames()
		{
			return _blobProvider.List(CloudServiceStateName.GetPrefix())
				.Select(name => name.ServiceName);
		}

		/// <summary>
		/// Enumerate the names of all user cloud services (system services are skipped).
		/// </summary>
		public IEnumerable<string> GetUserServiceNames()
		{
			var systemServices =
				new[] { typeof(GarbageCollectorService), typeof(DelayedQueueService), typeof(MonitoringService) }
					.Select(type => type.FullName)
					.ToList();

			return GetServiceNames()
				.Where(service => !systemServices.Contains(service));
		}

		/// <summary>
		/// Set or update the state of a cloud service
		/// </summary>
		public void SetServiceState(string serviceName, CloudServiceState state)
		{
			var blobName = new CloudServiceStateName(serviceName);

			_blobProvider.PutBlob<CloudServiceState?>(blobName, state);
		}

		/// <summary>
		/// Toggle the state of a cloud service
		/// </summary>
		public void ToggleServiceState(string serviceName)
		{
			var blobName = new CloudServiceStateName(serviceName);

			_blobProvider.UpdateIfNotModified<CloudServiceState?>(
				blobName,
				s => s.HasValue
					? (s.Value == CloudServiceState.Started ? CloudServiceState.Stopped : CloudServiceState.Started)
					: CloudServiceState.Started);
		}

		/// <summary>
		/// Remove the state information of a cloud service
		/// </summary>
		public void RemoveServiceState(string serviceName)
		{
			var blobName = new CloudServiceStateName(serviceName);

			_blobProvider.DeleteBlob(blobName);
		}
	}
}