﻿#region Copyright (c) Lokad 2010
// This code is released under the terms of the new BSD licence.
// URL: http://www.lokad.com/
#endregion

using System.Collections.Generic;
using System.Data.Services.Client;
using System.Linq;
using System.Text;
using System.Web;
using Microsoft.WindowsAzure.StorageClient;

namespace Lokad.Cloud.Storage.Azure
{
	/// <summary>Implementation based on the Table Storage of Windows Azure.</summary>
	public class TableStorageProvider : ITableStorageProvider
	{
		// HACK: those tokens will probably be provided as constants in the StorageClient library
		const int MaxEntityTransactionCount = 100;

		// HACK: Lowering the maximal payload, to avoid corner cases #117 (ContentLengthExceeded)
		// [vermorel] 128kB is purely arbitrary, only taken as a reasonable safety margin
		const int MaxEntityTransactionPayload = 4 * 1024 * 1024 - 128 * 1024; // 4 MB - 128kB

		const string ContinuationNextRowKeyToken = "x-ms-continuation-NextRowKey";
		const string ContinuationNextPartitionKeyToken = "x-ms-continuation-NextPartitionKey";
		const string NextRowKeyToken = "NextRowKey";
		const string NextPartitionKeyToken = "NextPartitionKey";

		readonly CloudTableClient _tableStorage;
		readonly IBinaryFormatter _formatter;
		readonly ActionPolicy _storagePolicy;

		/// <summary>IoC constructor.</summary>
		public TableStorageProvider(CloudTableClient tableStorage, IBinaryFormatter formatter)
		{
			_tableStorage = tableStorage;
			_formatter = formatter;
			_storagePolicy = AzurePolicies.TransientTableErrorBackOff;
		}

		public bool CreateTable(string tableName)
		{
			var flag = false;
			AzurePolicies.SlowInstantiation.Do(() =>
				flag = _tableStorage.CreateTableIfNotExist(tableName));

			return flag;
		}

		public bool DeleteTable(string tableName)
		{
			var flag = false;
			AzurePolicies.SlowInstantiation.Do(() =>
				flag = _tableStorage.DeleteTableIfExist(tableName));

			return flag;
		}

		public IEnumerable<string> GetTables()
		{
			return _tableStorage.ListTables();
		}

		public IEnumerable<CloudEntity<T>> Get<T>(string tableName)
		{
			Enforce.That(() => tableName);

			var context = _tableStorage.GetDataServiceContext();
			context.MergeOption = MergeOption.NoTracking;

			return GetInternal<T>(context, tableName, Maybe.String);
		}

		public IEnumerable<CloudEntity<T>> Get<T>(string tableName, string partitionKey)
		{
			Enforce.That(() => tableName);
			Enforce.That(() => partitionKey);
			Enforce.That(!partitionKey.Contains("'"), "Incorrect char in partitionKey.");

			var filter = string.Format("(PartitionKey eq '{0}')", HttpUtility.UrlEncode(partitionKey));

			var context = _tableStorage.GetDataServiceContext();
			context.MergeOption = MergeOption.NoTracking;

			return GetInternal<T>(context, tableName, filter);
		}

		public IEnumerable<CloudEntity<T>> Get<T>(string tableName, string partitionKey, string startRowKey, string endRowKey)
		{
			Enforce.That(() => tableName);
			Enforce.That(() => partitionKey);
			Enforce.That(!partitionKey.Contains("'"), "Incorrect char in partitionKey.");
			Enforce.That(!(startRowKey != null && startRowKey.Contains("'")), "Incorrect char in startRowKey.");
			Enforce.That(!(endRowKey != null && endRowKey.Contains("'")), "Incorrect char in endRowKey.");

			var filter = string.Format("(PartitionKey eq '{0}')", HttpUtility.UrlEncode(partitionKey));

			// optional starting range constraint
			if (!string.IsNullOrEmpty(startRowKey))
			{
				// ge = GreaterThanOrEqual (inclusive)
				filter += string.Format(" and (RowKey ge '{0}')", HttpUtility.UrlEncode(startRowKey));
			}

			if (!string.IsNullOrEmpty(endRowKey))
			{
				// lt = LessThan (exclusive)
				filter += string.Format(" and (RowKey lt '{0}')", HttpUtility.UrlEncode(endRowKey));
			}

			var context = _tableStorage.GetDataServiceContext();
			context.MergeOption = MergeOption.NoTracking;

			return GetInternal<T>(context, tableName, filter);
		}

		public IEnumerable<CloudEntity<T>> Get<T>(string tableName, string partitionKey, IEnumerable<string> rowKeys)
		{
			Enforce.That(() => tableName);
			Enforce.That(() => partitionKey);
			Enforce.That(!partitionKey.Contains("'"), "Incorrect char in partitionKey.");

			var context = _tableStorage.GetDataServiceContext();
			context.MergeOption = MergeOption.NoTracking;

			foreach (var slice in rowKeys.Slice(MaxEntityTransactionCount))
			{
				// work-around the limitation of ADO.NET that does not provide a native way
				// of query a set of specified entities directly.
				var builder = new StringBuilder();
				builder.Append(string.Format("(PartitionKey eq '{0}') and (", HttpUtility.UrlEncode(partitionKey)));
				for (int i = 0; i < slice.Length; i++)
				{
					// in order to avoid SQL-injection-like problems 
					Enforce.That(!slice[i].Contains("'"), "Incorrect char in rowKey.");

					builder.Append(string.Format("(RowKey eq '{0}')", HttpUtility.UrlEncode(slice[i])));
					if (i < slice.Length - 1)
					{
						builder.Append(" or ");
					}
				}
				builder.Append(")");

				foreach(var entity in GetInternal<T>(context, tableName, builder.ToString()))
				{
					yield return entity;
				}
			}
		}

		private IEnumerable<CloudEntity<T>> GetInternal<T>(TableServiceContext context, string tableName, Maybe<string> filter)
		{
			string continuationRowKey = null;
			string continuationPartitionKey = null;

			do
			{
				var query = context.CreateQuery<FatEntity>(tableName);

				if (filter.HasValue)
				{
					query = query.AddQueryOption("$filter", filter.Value);
				}

				if (null != continuationRowKey)
				{
					query = query.AddQueryOption(NextRowKeyToken, continuationRowKey)
						.AddQueryOption(NextPartitionKeyToken, continuationPartitionKey);
				}

				QueryOperationResponse response = null;
				FatEntity[] fatEntities = null;

				_storagePolicy.Do(() =>
					{
						try
						{
							response = query.Execute() as QueryOperationResponse;
							fatEntities = ((IEnumerable<FatEntity>)response).ToArray();
						}
						catch (DataServiceQueryException ex)
						{
							// if the table does not exist, there is nothing to return
							var errorCode = AzurePolicies.GetErrorCode(ex);
							if (TableErrorCodeStrings.TableNotFound == errorCode)
							{
								fatEntities = new FatEntity[0];
								return;
							}

							throw;
						}
					});

				foreach (var fatEntity in fatEntities)
				{
					yield return FatEntity.Convert<T>(fatEntity, _formatter);
				}

				if (null != response && response.Headers.ContainsKey(ContinuationNextRowKeyToken))
				{
					continuationRowKey = response.Headers[ContinuationNextRowKeyToken];
					continuationPartitionKey = response.Headers[ContinuationNextPartitionKeyToken];
				}
				else
				{
					continuationRowKey = null;
					continuationPartitionKey = null;
				}

			} while (null != continuationRowKey);
		}

		public void Insert<T>(string tableName, IEnumerable<CloudEntity<T>> entities)
		{
			entities.GroupBy(e => e.PartitionKey)
				.ForEach(g => InsertInternal(tableName, g));
		}

		void InsertInternal<T>(string tableName, IEnumerable<CloudEntity<T>> entities)
		{
			var context = _tableStorage.GetDataServiceContext();
			context.MergeOption = MergeOption.NoTracking;

			var fatEntities = entities.Select(e => FatEntity.Convert(e, _formatter));

			var noBatchMode = false;

			foreach (var slice in SliceEntities(fatEntities))
			{
				foreach (var fatEntity in slice)
				{
					context.AddObject(tableName, fatEntity);
				}

				_storagePolicy.Do(() =>
					{
						try
						{
							// HACK: nested try/catch
							try
							{
								context.SaveChanges(noBatchMode ? SaveChangesOptions.None : SaveChangesOptions.Batch);
							}
								// special casing the need for table instantiation
							catch (DataServiceRequestException ex)
							{
								var errorCode = AzurePolicies.GetErrorCode(ex);
								if (errorCode == TableErrorCodeStrings.TableNotFound)
								{
									AzurePolicies.SlowInstantiation.Do(() =>
										{
											_tableStorage.CreateTableIfNotExist(tableName);
											context.SaveChanges(noBatchMode ? SaveChangesOptions.None : SaveChangesOptions.Batch);
										});
								}
								else
								{
									throw;
								}
							}
						}
						catch (DataServiceRequestException ex)
						{
							var errorCode = AzurePolicies.GetErrorCode(ex);

							if (errorCode == StorageErrorCodeStrings.OperationTimedOut)
							{
								// if batch does not work, then split into elementary requests
								// PERF: it would be better to split the request in two and retry
								context.SaveChanges();
								noBatchMode = true;
							}
								// HACK: undocumented code returned by the Table Storage
							else if (errorCode == "ContentLengthExceeded")
							{
								context.SaveChanges();
								noBatchMode = true;
							}
							else
							{
								throw;
							}
						}
						catch (DataServiceQueryException ex)
						{
							// HACK: code dupplicated

							var errorCode = AzurePolicies.GetErrorCode(ex);

							if (errorCode == StorageErrorCodeStrings.OperationTimedOut)
							{
								// if batch does not work, then split into elementary requests
								// PERF: it would be better to split the request in two and retry
								context.SaveChanges();
								noBatchMode = true;
							}
							else
							{
								throw;
							}
						}
					});
			}
		}

		public void Update<T>(string tableName, IEnumerable<CloudEntity<T>> entities)
		{
			entities.GroupBy(e => e.PartitionKey)
				.ForEach(g => UpdateInternal(tableName, g));
		}

		void UpdateInternal<T>(string tableName, IEnumerable<CloudEntity<T>> entities)
		{
			var context = _tableStorage.GetDataServiceContext();

			var fatEntities = entities.Select(e => FatEntity.Convert(e, _formatter));

			var noBatchMode = false;

			foreach (var slice in SliceEntities(fatEntities))
			{
				foreach (var fatEntity in slice)
				{
					// entities should be updated in a single round-trip
					context.AttachTo(tableName, fatEntity, "*");
					context.UpdateObject(fatEntity);
				}

				_storagePolicy.Do(() =>
					{
						try
						{
							context.SaveChanges(noBatchMode ? SaveChangesOptions.None : SaveChangesOptions.Batch);
						}
						catch (DataServiceRequestException ex)
						{
							var errorCode = AzurePolicies.GetErrorCode(ex);

							if (errorCode == StorageErrorCodeStrings.OperationTimedOut)
							{
								// if batch does not work, then split into elementary requests
								// PERF: it would be better to split the request in two and retry
								context.SaveChanges();
								noBatchMode = true;
							}
							else
							{
								throw;
							}
						}
						catch (DataServiceQueryException ex)
						{
							// HACK: code dupplicated

							var errorCode = AzurePolicies.GetErrorCode(ex);

							if (errorCode == StorageErrorCodeStrings.OperationTimedOut)
							{
								// if batch does not work, then split into elementary requests
								// PERF: it would be better to split the request in two and retry
								context.SaveChanges();
								noBatchMode = true;
							}
							else
							{
								throw;
							}
						}
					});
			}
		}

		public void Upsert<T>(string tableName, IEnumerable<CloudEntity<T>> entities)
		{
			entities.GroupBy(e => e.PartitionKey)
				.ForEach(g => UpsertInternal(tableName, g));
		}

		// HACK: no 'upsert' (update or insert) available at the time
		// http://social.msdn.microsoft.com/Forums/en-US/windowsazure/thread/4b902237-7cfb-4d48-941b-4802864fc274

		/// <remarks>Upsert is making several storage calls to emulate the 
		/// missing semantic from the Table Storage.</remarks>
		void UpsertInternal<T>(string tableName, IEnumerable<CloudEntity<T>> entities)
		{
			// checking for entities that already exist
			var partitionKey = entities.First().PartitionKey;
			var existingKeys =
				Get<T>(tableName, partitionKey, entities.Select(e => e.RowRey))
					.ToSet(e => e.RowRey);

			// inserting or updating depending on the presence of the keys
			Insert(tableName, entities.Where(e => !existingKeys.Contains(e.RowRey)));
			Update(tableName, entities.Where(e => existingKeys.Contains(e.RowRey)));
		}

		/// <summary>Slice entities according the payload limitation of
		/// the transaction group, plus the maximal number of entities to
		/// be embedded into a single transaction.</summary>
		static IEnumerable<FatEntity[]> SliceEntities(IEnumerable<FatEntity> entities)
		{
			var accumulator = new List<FatEntity>(100);
			var payload = 0;
			foreach (var entity in entities)
			{
				var entityPayLoad = entity.GetPayload();

				if (accumulator.Count >= MaxEntityTransactionCount ||
					payload + entityPayLoad >= MaxEntityTransactionPayload)
				{
					yield return accumulator.ToArray();
					accumulator.Clear();
					payload = 0;
				}

				accumulator.Add(entity);
				payload += entityPayLoad;
			}

			if (accumulator.Count > 0)
			{
				yield return accumulator.ToArray();
			}
		}

		public void Delete<T>(string tableName, string partitionKey, IEnumerable<string> rowKeys)
		{
			var context = _tableStorage.GetDataServiceContext();

			// CAUTION: make sure to get rid of potential duplicate in rowkeys.
			// (otherwise insertion in 'context' is likely to fail)
			foreach (var s in rowKeys.Distinct().Slice(MaxEntityTransactionCount))
			{
				var slice = s;

				DeletionStart: // 'slice' might have been refreshed if some entities were already deleted

				foreach (var rowKey in slice)
				{
					// Deleting entities in 1 roundtrip
					// http://blog.smarx.com/posts/deleting-entities-from-windows-azure-without-querying-first
					var mock = new FatEntity
						{
							PartitionKey = partitionKey,
							RowKey = rowKey
						};

					context.AttachTo(tableName, mock, "*");
					context.DeleteObject(mock);

				}

				try // HACK: [vermorel] if a single entity is missing, then the whole batch operation is aborded
				{

					try // HACK: nested try/catch to handle the special case where the table is missing
					{
						_storagePolicy.Do(() =>
							context.SaveChanges(SaveChangesOptions.Batch));
					}
					catch (DataServiceRequestException ex)
					{
						// if the table is missing, no need to go on with the deletion
						var errorCode = AzurePolicies.GetErrorCode(ex);
						if (TableErrorCodeStrings.TableNotFound == errorCode)
						{
							return;
						}

						throw;
					}
				}
					// if some entities exist
				catch (DataServiceRequestException ex)
				{
					var errorCode = AzurePolicies.GetErrorCode(ex);

					// HACK: Table Storage both implement a bizarre non-idempotent semantic
					// but in addition, it throws a non-documented exception as well. 
					if (errorCode != "ResourceNotFound")
					{
						throw;
					}

					slice = Get<T>(tableName, partitionKey, slice).Select(e => e.RowRey).ToArray();

					// entities with same name will be added again
					context = _tableStorage.GetDataServiceContext();

					// HACK: [vermorel] yes, gotos are horrid, but other solutions are worst here.
					goto DeletionStart;
				}
			}
		}
	}
}