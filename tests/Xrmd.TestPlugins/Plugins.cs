using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace Xrmd.TestPlugins
{
    /// <summary><c>"attr=value"</c> sets <c>Target[attr] = value</c> (<c>Targets[0]</c> on a bulk message, #76).</summary>
    public class SetAttributePlugin : IPlugin
    {
        private readonly string _config;

        public SetAttributePlugin(string unsecure, string secure) { _config = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var target = context.InputParameters.Contains("Target")
                ? (Entity)context.InputParameters["Target"]
                : ((EntityCollection)context.InputParameters["Targets"]).Entities[0];
            var i = _config.IndexOf('=');
            target[_config.Substring(0, i)] = _config.Substring(i + 1);
        }
    }

    /// <summary>
    /// <c>"attr=type:value"</c> sets <c>Target[attr]</c> to the typed value, with type <c>decimal</c>, <c>int</c> or <c>option</c>
    /// (an <see cref="OptionSetValue"/>), parsed with the invariant culture.
    /// </summary>
    public class SetTypedAttributePlugin : IPlugin
    {
        private readonly string _config;

        public SetTypedAttributePlugin(string unsecure, string secure) { _config = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var target = (Entity)context.InputParameters["Target"];
            var i = _config.IndexOf('=');
            var j = _config.IndexOf(':', i + 1);
            var type = _config.Substring(i + 1, j - i - 1);
            var text = _config.Substring(j + 1);
            object value;
            switch (type)
            {
                case "decimal": value = decimal.Parse(text, CultureInfo.InvariantCulture); break;
                case "int": value = int.Parse(text, CultureInfo.InvariantCulture); break;
                case "option": value = new OptionSetValue(int.Parse(text, CultureInfo.InvariantCulture)); break;
                default: throw new InvalidPluginExecutionException("Unknown type " + type);
            }
            target[_config.Substring(0, i)] = value;
        }
    }

    /// <summary><c>invalid:msg</c>, <c>crash:msg</c> or <c>sleep:ms</c>.</summary>
    public class ThrowPlugin : IPlugin
    {
        private readonly string _config;

        public ThrowPlugin(string unsecure, string secure) { _config = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            tracing.Trace("ThrowPlugin start");
            if (_config.StartsWith("invalid:", StringComparison.Ordinal))
                throw new InvalidPluginExecutionException(_config.Substring("invalid:".Length));
            if (_config.StartsWith("crash:", StringComparison.Ordinal))
                throw new InvalidOperationException(_config.Substring("crash:".Length));
            if (_config.StartsWith("sleep:", StringComparison.Ordinal))
                Thread.Sleep(int.Parse(_config.Substring("sleep:".Length), CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// <c>"{name}|{message}"</c>: throws an <c>InvalidPluginExecutionException</c> with the message when <c>Target["name"]</c> is
    /// the configured name (#76: one target of a bulk message fails).
    /// </summary>
    public class ThrowOnNamePlugin : IPlugin
    {
        private readonly string _name;
        private readonly string _message;

        public ThrowOnNamePlugin(string unsecure, string secure)
        {
            var i = unsecure.IndexOf('|');
            _name = unsecure.Substring(0, i);
            _message = unsecure.Substring(i + 1);
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var target = context.InputParameters.Contains("Target") ? context.InputParameters["Target"] as Entity : null;
            if (target != null && target.GetAttributeValue<string>("name") == _name)
                throw new InvalidPluginExecutionException(_message);
        }
    }

    /// <summary>
    /// Creates a record of the configured entity named <c>Message|Stage|Depth|PrimaryEntityId</c>; config
    /// <c>"entity|system"</c> creates it with <c>CreateOrganizationService(null)</c> (SYSTEM), <c>"entity|user:{guid}"</c> (or
    /// <c>"entity|{guid}"</c>) with <c>CreateOrganizationService(guid)</c>.
    /// </summary>
    public class CreateRecordPlugin : IPlugin
    {
        private readonly string _entity;
        private readonly bool _system;
        private readonly Guid? _user;

        public CreateRecordPlugin(string unsecure, string secure)
        {
            var parts = unsecure.Split('|');
            _entity = parts[0];
            _system = parts.Length > 1 && parts[1] == "system";
            // "<entity>|user:<guid>" or "<entity>|<guid>": the service acts as that user.
            if (parts.Length > 1 && !_system)
                _user = new Guid(parts[1].StartsWith("user:", StringComparison.Ordinal) ? parts[1].Substring(5) : parts[1]);
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(_system ? null : _user ?? context.UserId);
            var e = new Entity(_entity);
            e["pb_name"] = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}",
                context.MessageName, context.Stage, context.Depth, context.PrimaryEntityId);
            service.Create(e);
        }
    }

    /// <summary>
    /// <c>"entity|{guid}"</c>: upserts the record of the entity with that id (<c>pb_name = "upsert"</c>) through its organization
    /// service's <c>Execute</c> with an <c>UpsertRequest</c> (#76). A fault is not caught.
    /// </summary>
    public class UpsertRecordPlugin : IPlugin
    {
        private readonly string _entity;
        private readonly Guid _id;

        public UpsertRecordPlugin(string unsecure, string secure)
        {
            var parts = unsecure.Split('|');
            _entity = parts[0];
            _id = new Guid(parts[1]);
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            service.Execute(new Microsoft.Xrm.Sdk.Messages.UpsertRequest { Target = new Entity(_entity, _id) { ["pb_name"] = "upsert" } });
        }
    }

    /// <summary>Records everything it can see of the execution context into a record of the configured entity.</summary>
    public class ContextRecorderPlugin : IPlugin
    {
        private readonly string _entity;

        public ContextRecorderPlugin(string unsecure, string secure) { _entity = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);

            var target = context.InputParameters.Contains("Target") ? context.InputParameters["Target"] as Entity : null;
            var targetAttrs = target == null ? "" : string.Join(",", target.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal));
            var lines = new List<string>
            {
                "target=" + targetAttrs,
                "primary=" + context.PrimaryEntityName,
                "pre=" + Images(context.PreEntityImages),
                "post=" + Images(context.PostEntityImages),
                "shared=" + string.Join(",", context.SharedVariables.Keys.OrderBy(k => k, StringComparer.Ordinal)),
                "parent=" + (context.ParentContext == null ? "" : context.ParentContext.MessageName),
                "user=" + context.UserId,
                "initiating=" + context.InitiatingUserId,
                "output=" + string.Join(",", context.OutputParameters.Keys.OrderBy(k => k, StringComparer.Ordinal)),
                "intx=" + context.IsInTransaction,
                "preowner=" + Owner(context.PreEntityImages),
                "postowner=" + Owner(context.PostEntityImages),
                "useroid=" + ((IPluginExecutionContext2)context).UserAzureActiveDirectoryObjectId,
                "initiatingoid=" + ((IPluginExecutionContext2)context).InitiatingUserAzureActiveDirectoryObjectId,
                "authenticated=" + ((IPluginExecutionContext3)context).AuthenticatedUserId,
                "targetcollections=" + Collections(target),
                "postcollections=" + string.Join(";", context.PostEntityImages.OrderBy(i => i.Key, StringComparer.Ordinal)
                    .Select(i => i.Key + ":" + Collections(i.Value))),
                // #76: the bulk view (Targets, the image collections of IPluginExecutionContext4, the Ids output).
                "targets=" + (context.InputParameters.Contains("Targets") && context.InputParameters["Targets"] is EntityCollection targets
                    ? targets.Entities.Count.ToString(CultureInfo.InvariantCulture) : ""),
                "precollections=" + CollectionCount(((IPluginExecutionContext4)context).PreEntityImagesCollection),
                // "postcollections=" already names the party lists of the post images above.
                "postimagecollections=" + CollectionCount(((IPluginExecutionContext4)context).PostEntityImagesCollection),
                "ids=" + (context.OutputParameters.Contains("Ids") && context.OutputParameters["Ids"] is Guid[] ids
                    ? ids.Length.ToString(CultureInfo.InvariantCulture) : ""),
                // Each image collection as alias:name of its images, collections separated by ";" (#76, index alignment).
                "preimagenames=" + CollectionNames(((IPluginExecutionContext4)context).PreEntityImagesCollection),
                "postimagenames=" + CollectionNames(((IPluginExecutionContext4)context).PostEntityImagesCollection),
                "parentstage=" + (context.ParentContext == null ? "" : context.ParentContext.Stage.ToString(CultureInfo.InvariantCulture)),
                // The scalar values of the post images as alias:name=value (plan 2.12a: a calculated column's value in an image).
                "postvalues=" + Values(context.PostEntityImages),
            };

            var e = new Entity(_entity);
            e["pb_name"] = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}",
                context.MessageName, context.Stage, context.Mode, context.Depth);
            e["pb_data"] = string.Join("\n", lines);
            service.Create(e);

            context.SharedVariables["recorder"] = context.Stage;
        }

        /// <summary>
        /// Each EntityCollection attribute (a party list) as <c>name:count:first partyid</c>, sorted by name, comma separated.
        /// </summary>
        private static string Collections(Entity? e)
        {
            if (e == null) return "";
            return string.Join(",", e.Attributes.Where(a => a.Value is EntityCollection).OrderBy(a => a.Key, StringComparer.Ordinal).Select(a =>
            {
                var list = (EntityCollection)a.Value;
                var first = list.Entities.Count == 0 ? null : list.Entities[0].GetAttributeValue<EntityReference>("partyid");
                return a.Key + ":" + list.Entities.Count + ":" + (first == null ? "" : first.Id.ToString());
            }));
        }

        private static string CollectionCount(EntityImageCollection[] collections)
        {
            return collections == null ? "" : collections.Length.ToString(CultureInfo.InvariantCulture);
        }

        private static string CollectionNames(EntityImageCollection[] collections)
        {
            return collections == null ? "" : string.Join(";", collections.Select(c => string.Join(",",
                c.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => i.Key + ":" + i.Value.GetAttributeValue<string>("name")))));
        }

        private static string Owner(EntityImageCollection images)
        {
            var owner = images.Select(i => i.Value.GetAttributeValue<EntityReference>("ownerid")).FirstOrDefault(o => o != null);
            return owner == null ? "" : owner.Id.ToString();
        }

        private static string Values(EntityImageCollection images)
        {
            return string.Join(";", images.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i =>
                i.Key + ":" + string.Join(",", i.Value.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal)
                    .Where(a => a.Value is string || a.Value is int || a.Value is decimal || a.Value is bool || a.Value is Money)
                    .Select(a => a.Key + "=" + (a.Value is Money money
                        ? money.Value.ToString(CultureInfo.InvariantCulture)
                        : Convert.ToString(a.Value, CultureInfo.InvariantCulture))))));
        }

        private static string Images(EntityImageCollection images)
        {
            return string.Join(";", images.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i =>
                i.Key + ":" + string.Join(",", i.Value.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal))));
        }
    }

    /// <summary>Post-operation Update that updates the same record again (infinite recursion).</summary>
    public class UpdateSelfPlugin : IPlugin
    {
        public UpdateSelfPlugin() { }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            var e = new Entity(context.PrimaryEntityName, context.PrimaryEntityId);
            e["pb_count"] = context.Depth;
            service.Update(e);
        }
    }

    /// <summary>
    /// <c>"attr=value"</c>: updates <c>attr</c> of the primary record to the string <c>value</c> with
    /// <c>CreateOrganizationService(context.UserId)</c>, so the Update runs as the user the step runs as (register it on
    /// PostOperation Create).
    /// </summary>
    public class UpdateAttributePlugin : IPlugin
    {
        private readonly string _config;

        public UpdateAttributePlugin(string unsecure, string secure) { _config = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            var i = _config.IndexOf('=');
            var e = new Entity(context.PrimaryEntityName, context.PrimaryEntityId);
            e[_config.Substring(0, i)] = _config.Substring(i + 1);
            service.Update(e);
        }
    }

    /// <summary>
    /// Retrieves a record of the primary entity that does not exist, catches the <c>FaultException</c> and continues:
    /// sets <c>Target["pb_data"] = "caught {ErrorCode}"</c> (register it on PreOperation Create/Update).
    /// </summary>
    public class TryRetrieveMissingPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            string outcome;
            try
            {
                service.Retrieve(context.PrimaryEntityName, Guid.NewGuid(), new ColumnSet("pb_name"));
                outcome = "found";
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                outcome = "caught " + ex.Detail.ErrorCode.ToString(CultureInfo.InvariantCulture);
            }

            ((Entity)context.InputParameters["Target"])["pb_data"] = outcome;
        }
    }

    /// <summary>
    /// <c>"sleepMs|key"</c>: sleeps (past the plug-in timeout), then tries to create a <c>pb_pipelog</c> with its
    /// organization service and stores the outcome (<c>ok</c>, <c>fault {ErrorCode}</c> or <c>other {ExceptionType}</c>)
    /// in the process environment variable <c>key</c>.
    /// </summary>
    public class LateServiceCallPlugin : IPlugin
    {
        private readonly int _sleep;
        private readonly string _key;

        public LateServiceCallPlugin(string unsecure, string secure)
        {
            var parts = unsecure.Split('|');
            _sleep = int.Parse(parts[0], CultureInfo.InvariantCulture);
            _key = parts[1];
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            Environment.SetEnvironmentVariable(_key + "_STARTED", "1");
            Thread.Sleep(_sleep);
            string outcome;
            try
            {
                service.Create(new Entity("pb_pipelog") { ["pb_name"] = "late" });
                outcome = "ok";
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                outcome = "fault " + ex.Detail.ErrorCode.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                outcome = "other " + ex.GetType().FullName;
            }

            Environment.SetEnvironmentVariable(_key, outcome);
        }
    }

    /// <summary>
    /// Executes the request named by the configuration through its organization service, catches the
    /// <c>FaultException</c> and sets <c>Target["pb_data"] = "caught {ErrorCode}|{Message}"</c> (or <c>"ok"</c>).
    /// </summary>
    public class TryExecutePlugin : IPlugin
    {
        private readonly string _request;

        public TryExecutePlugin(string unsecure, string secure) { _request = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            string outcome;
            try
            {
                service.Execute(new OrganizationRequest(_request));
                outcome = "ok";
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                outcome = "caught " + ex.Detail.ErrorCode.ToString(CultureInfo.InvariantCulture) + "|" + ex.Detail.Message;
            }

            ((Entity)context.InputParameters["Target"])["pb_data"] = outcome;
        }
    }

    /// <summary>
    /// <c>"{message}|{entity}|{attribute}"</c>: issues the bulk message (#76, ruling B12) with one target of the entity whose
    /// attribute is <c>"from plugin"</c> through its organization service, catches the <c>FaultException</c> and sets
    /// <c>Target["pb_data"] = "caught {ErrorCode}|{Message}"</c> (or <c>"ok"</c>).
    /// </summary>
    public class TryBulkPlugin : IPlugin
    {
        private readonly string _message;
        private readonly string _entity;
        private readonly string _attribute;

        public TryBulkPlugin(string unsecure, string secure)
        {
            var parts = unsecure.Split('|');
            _message = parts[0];
            _entity = parts[1];
            _attribute = parts[2];
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            var targets = new EntityCollection { EntityName = _entity };
            targets.Entities.Add(new Entity(_entity) { [_attribute] = "from plugin" });
            var request = new OrganizationRequest(_message);
            request["Targets"] = targets;
            string outcome;
            try
            {
                service.Execute(request);
                outcome = "ok";
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                outcome = "caught " + ex.Detail.ErrorCode.ToString(CultureInfo.InvariantCulture) + "|" + ex.Detail.Message;
            }

            ((Entity)context.InputParameters["Target"])["pb_data"] = outcome;
        }
    }

    /// <summary>
    /// <c>"{email}|{suppress}"</c>: creates a contact with that <c>emailaddress1</c> through its organization service's
    /// <c>Execute</c>, with a <c>CreateRequest</c> whose <c>SuppressDuplicateDetection</c> is the given Boolean (plan 2.15). A fault
    /// is not caught.
    /// </summary>
    public class CreateContactWithDuplicateDetectionPlugin : IPlugin
    {
        private readonly string _email;
        private readonly bool _suppress;

        public CreateContactWithDuplicateDetectionPlugin(string unsecure, string secure)
        {
            var parts = unsecure.Split('|');
            _email = parts[0];
            _suppress = bool.Parse(parts[1]);
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            var request = new CreateRequest { Target = new Entity("contact") { ["lastname"] = "Nested", ["emailaddress1"] = _email } };
            request["SuppressDuplicateDetection"] = _suppress;
            service.Execute(request);
        }
    }

    /// <summary>
    /// <c>"entity|{guid}"</c>: tries to create a record of the entity with that id, catches the <c>FaultException</c> and
    /// sets <c>Target["pb_data"] = "caught {ErrorCode}"</c> (or <c>"ok"</c>).
    /// </summary>
    public class TryCreatePlugin : IPlugin
    {
        private readonly string _entity;
        private readonly Guid _id;

        public TryCreatePlugin(string unsecure, string secure)
        {
            var parts = unsecure.Split('|');
            _entity = parts[0];
            _id = new Guid(parts[1]);
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            string outcome;
            try
            {
                service.Create(new Entity(_entity, _id) { ["pb_name"] = "try" });
                outcome = "ok";
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                outcome = "caught " + ex.Detail.ErrorCode.ToString(CultureInfo.InvariantCulture);
            }

            ((Entity)context.InputParameters["Target"])["pb_data"] = outcome;
        }
    }

    /// <summary>
    /// Calls WhoAmI, RetrieveEntity (the primary entity) and RetrieveAttribute (<c>pb_name</c>) with the caller's service
    /// (<c>"system"</c>: with <c>CreateOrganizationService(null)</c>) and sets
    /// <c>Target["pb_data"] = "user={UserId}|bu={BusinessUnitId}|org={OrganizationId}|entity={LogicalName}|attr={LogicalName}"</c>.
    /// </summary>
    public class NonPipelineCallsPlugin : IPlugin
    {
        private readonly bool _system;

        public NonPipelineCallsPlugin(string unsecure, string secure) { _system = unsecure == "system"; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(_system ? (Guid?)null : context.UserId);
            var me = (WhoAmIResponse)service.Execute(new WhoAmIRequest());
            var entity = (RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
            {
                LogicalName = context.PrimaryEntityName,
                EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity,
            });
            var attribute = (RetrieveAttributeResponse)service.Execute(new RetrieveAttributeRequest
            {
                EntityLogicalName = context.PrimaryEntityName,
                LogicalName = "pb_name",
            });
            ((Entity)context.InputParameters["Target"])["pb_data"] = string.Format(CultureInfo.InvariantCulture,
                "user={0}|bu={1}|org={2}|entity={3}|attr={4}", me.UserId, me.BusinessUnitId, me.OrganizationId,
                entity.EntityMetadata.LogicalName, attribute.AttributeMetadata.LogicalName);
        }
    }

    /// <summary>
    /// Plan 3.5 UI-1b final review, I3: calls RetrievePrincipalAccess (the caller on the Target) and RetrieveSharedPrincipalsAndAccess
    /// (the Target) with the caller's service and creates a record of the configured entity named
    /// <c>"rights={AccessRights as int}|shares={count}"</c>. Registered on PostOperation Create, the Target is a row the plug-in's
    /// transaction has not committed yet. A fault is not caught.
    /// </summary>
    public class AccessMessagesPlugin : IPlugin
    {
        private readonly string _entity;

        public AccessMessagesPlugin(string unsecure, string secure) { _entity = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            var target = new EntityReference(context.PrimaryEntityName, context.PrimaryEntityId);
            var rights = (RetrievePrincipalAccessResponse)service.Execute(new RetrievePrincipalAccessRequest
            {
                Target = target,
                Principal = new EntityReference("systemuser", context.UserId),
            });
            var shares = (RetrieveSharedPrincipalsAndAccessResponse)service.Execute(new RetrieveSharedPrincipalsAndAccessRequest { Target = target });
            var e = new Entity(_entity);
            e["pb_name"] = string.Format(CultureInfo.InvariantCulture, "rights={0}|shares={1}", (int)rights.AccessRights,
                shares.PrincipalAccesses.Length);
            service.Create(e);
        }
    }

    /// <summary>
    /// Calls RetrieveRecordChangeHistory on the Target with the caller's service and sets
    /// <c>Target["pb_data"] = "total={TotalRecordCount}|first={type of the newest detail}|name={its NewValue pb_name}"</c>.
    /// </summary>
    public class AuditHistoryPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            var target = (Entity)context.InputParameters["Target"];
            var response = (RetrieveRecordChangeHistoryResponse)service.Execute(new RetrieveRecordChangeHistoryRequest
            {
                Target = target.ToEntityReference(),
                PagingInfo = new PagingInfo { PageNumber = 1, Count = 10, ReturnTotalRecordCount = true },
            });
            var collection = response.AuditDetailCollection;
            var first = collection.AuditDetails.FirstOrDefault();
            var name = first is AttributeAuditDetail attribute ? attribute.NewValue.GetAttributeValue<string>("pb_name") : null;
            target["pb_data"] = string.Format(CultureInfo.InvariantCulture, "total={0}|first={1}|name={2}", collection.TotalRecordCount,
                first == null ? "none" : first.GetType().Name, name);
        }
    }

    /// <summary>
    /// Uses <c>Xrmd.TestPlugins.Dependency</c>, which is not deployed with this assembly (as a dependency left out of an
    /// ILMerge): <c>Execute</c> fails with a <see cref="System.IO.FileNotFoundException"/>. Its type and constructor do not
    /// need the dependency, so it registers fine.
    /// </summary>
    public class MissingDependencyPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            ((Entity)context.InputParameters["Target"])["pb_data"] = Dependency.Helper.Value();
        }
    }

    /// <summary>
    /// With <c>CreateOrganizationService(context.UserId)</c>, tries to create a system-managed access team (a write only the record
    /// team messages may make, plan 2.16 ruling A4) and sets <c>Target["pb_data"] = "caught {ErrorCode}"</c> (or <c>"ok"</c>).
    /// Config: the <c>businessunitid</c> of the team.
    /// </summary>
    public class TryCreateSystemManagedTeamPlugin : IPlugin
    {
        private readonly Guid _businessUnit;

        public TryCreateSystemManagedTeamPlugin(string unsecure, string secure) { _businessUnit = new Guid(unsecure); }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            string outcome;
            try
            {
                service.Create(new Entity("team")
                {
                    ["name"] = "plugin-" + Guid.NewGuid().ToString("N"),
                    ["teamtype"] = new OptionSetValue(1),
                    ["businessunitid"] = new EntityReference("businessunit", _businessUnit),
                    ["systemmanaged"] = true,
                });
                outcome = "ok";
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                outcome = "caught " + ex.Detail.ErrorCode.ToString(CultureInfo.InvariantCulture);
            }

            ((Entity)context.InputParameters["Target"])["pb_data"] = outcome;
        }
    }

    /// <summary>
    /// With <c>CreateOrganizationService(Guid.Empty)</c> (Learn: the same user as <c>context.UserId</c>), tries to update the
    /// record of the config <c>"entity|{guid}"</c> and sets <c>Target["pb_data"] = "caught {ErrorCode}"</c> (or <c>"ok"</c>).
    /// </summary>
    public class TryUpdateAsSameUserPlugin : IPlugin
    {
        private readonly string _entity;
        private readonly Guid _id;

        public TryUpdateAsSameUserPlugin(string unsecure, string secure)
        {
            var parts = unsecure.Split('|');
            _entity = parts[0];
            _id = new Guid(parts[1]);
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(Guid.Empty);
            string outcome;
            try
            {
                service.Update(new Entity(_entity, _id) { ["pb_name"] = "updated by plug-in" });
                outcome = "ok";
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                outcome = "caught " + ex.Detail.ErrorCode.ToString(CultureInfo.InvariantCulture);
            }

            ((Entity)context.InputParameters["Target"])["pb_data"] = outcome;
        }
    }

    /// <summary>Custom action plug-in: reverses <c>Text</c> into <c>Result</c>.</summary>
    public class EchoActionPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var text = (string)context.InputParameters["Text"];
            context.OutputParameters["Result"] = new string(text.Reverse().ToArray());
        }
    }

    /// <summary>
    /// Custom API plug-in (plan 15). Without configuration (the main operation): copies every input parameter but <c>Target</c>
    /// and <c>tag</c> to the output of the same name plus <c>Out</c> (<c>tag</c> to <c>tagOut</c>), and describes its context in the output <c>Context</c>
    /// (<c>Stage=..;PrimaryEntityName=..;PrimaryEntityId=..;UserId=..;Outputs=..</c>, the outputs being the keys it found).
    /// With the input <c>CreateThenFail</c> (a table name), it first creates a record of that table, then fails. With the input
    /// <c>Call</c> (a message name), it also calls that message with <c>Text</c> = "nested" and puts the call's <c>TextOut</c> in
    /// <c>CallOut</c>. As a step: <c>"input:x"</c> appends x to the input <c>Text</c>; <c>"output:x"</c> appends x to the output
    /// <c>TextOut</c> and rewrites <c>Context</c>.
    /// </summary>
    public class CustomApiEchoPlugin : IPlugin
    {
        private readonly string _config;

        public CustomApiEchoPlugin(string unsecure, string secure) { _config = unsecure ?? ""; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var outputsSeen = string.Join(",", context.OutputParameters.Keys.OrderBy(k => k, StringComparer.Ordinal));
            if (_config.StartsWith("input:", StringComparison.Ordinal))
            {
                context.InputParameters["Text"] = (string)context.InputParameters["Text"] + _config.Substring("input:".Length);
                return;
            }
            if (_config.StartsWith("output:", StringComparison.Ordinal))
            {
                context.OutputParameters["TextOut"] = (string)context.OutputParameters["TextOut"] + _config.Substring("output:".Length);
                context.OutputParameters["Context"] = Describe(context, outputsSeen);
                return;
            }

            var service = factory.CreateOrganizationService(context.UserId);
            if (context.InputParameters.Contains("CreateThenFail"))
            {
                var record = new Entity((string)context.InputParameters["CreateThenFail"]);
                record["pb_name"] = "created by the main operation";
                service.Create(record);
                throw new InvalidPluginExecutionException("The main operation failed after creating a record.");
            }

            foreach (var parameter in context.InputParameters)
                if (parameter.Key != "Target" && parameter.Key != "tag")
                    context.OutputParameters[parameter.Key + "Out"] = parameter.Value;
            if (context.InputParameters.Contains("tag")) context.OutputParameters["tagOut"] = context.InputParameters["tag"];
            if (context.InputParameters.Contains("Call"))
            {
                var nested = new OrganizationRequest((string)context.InputParameters["Call"]);
                nested["Text"] = "nested";
                context.OutputParameters["CallOut"] = service.Execute(nested).Results["TextOut"];
            }
            context.OutputParameters["Context"] = Describe(context, outputsSeen);
        }

        private static string Describe(IPluginExecutionContext context, string outputsSeen)
        {
            return string.Format(CultureInfo.InvariantCulture, "Stage={0};PrimaryEntityName={1};PrimaryEntityId={2};UserId={3};Outputs={4}",
                context.Stage, context.PrimaryEntityName, context.PrimaryEntityId, context.UserId, outputsSeen);
        }
    }

    /// <summary>Custom API plug-in (plan 15): sets the output <c>Count</c>, declared as an Integer, to a string.</summary>
    public class CustomApiWrongTypePlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            context.OutputParameters["Count"] = "three";
        }
    }

    /// <summary>
    /// Post-operation Retrieve/RetrieveMultiple: <c>"attr=value"</c> sets the attribute on <c>BusinessEntity</c> and on
    /// every entity of <c>BusinessEntityCollection</c>.
    /// </summary>
    public class OutputEditorPlugin : IPlugin
    {
        private readonly string _config;

        public OutputEditorPlugin(string unsecure, string secure) { _config = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var i = _config.IndexOf('=');
            var name = _config.Substring(0, i);
            var value = _config.Substring(i + 1);
            if (context.OutputParameters.Contains("BusinessEntity"))
                ((Entity)context.OutputParameters["BusinessEntity"])[name] = value;
            if (context.OutputParameters.Contains("BusinessEntityCollection"))
                foreach (var e in ((EntityCollection)context.OutputParameters["BusinessEntityCollection"]).Entities)
                    e[name] = value;
        }
    }

    /// <summary>
    /// <c>"schemaname"</c>: reads the environment variable through <c>IEnvironmentVariableService</c> and sets
    /// <c>Target["pb_data"]</c> to its value, or <c>"(null)"</c> when it has none (register it on PreOperation Create). A failure
    /// of the service is left to fail the plug-in.
    /// </summary>
    public class EnvironmentVariablePlugin : IPlugin
    {
        private readonly string _config;

        public EnvironmentVariablePlugin(string unsecure, string secure) { _config = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var variables = (IEnvironmentVariableService)serviceProvider.GetService(typeof(IEnvironmentVariableService));
            var value = variables.RetrieveEnvironmentVariableValue(_config);
            ((Entity)context.InputParameters["Target"])["pb_data"] = value ?? "(null)";
        }
    }

    /// <summary>An abstract plug-in base class (not a plug-in type itself).</summary>
    public abstract class PluginBase : IPlugin
    {
        public abstract void Execute(IServiceProvider serviceProvider);
    }

    /// <summary>Implements IPlugin only through <see cref="PluginBase"/>.</summary>
    public class DerivedPlugin : PluginBase
    {
        public override void Execute(IServiceProvider serviceProvider) { }
    }

    /// <summary>A public class that is not a plug-in.</summary>
    public class NotAPlugin
    {
    }
}
