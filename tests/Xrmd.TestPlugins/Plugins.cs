using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
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

        /// <summary>The attributes (name=value) of the parent context's Target, to show what a parent holds.</summary>
        private static string ParentTarget(IPluginExecutionContext? parent)
        {
            if (parent == null || !parent.InputParameters.Contains("Target")) return "";
            var e = parent.InputParameters["Target"] as Entity;
            return e == null ? "" : string.Join(",", e.Attributes.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
        }

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
                "parenttarget=" + ParentTarget(context.ParentContext),
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

    /// <summary>
    /// Plan 4.1c, task 7: reads or writes one shared variable. <c>"record|{entity}|{key}"</c> creates a record of the entity named
    /// <c>Message|Stage|Depth|value</c> (<c>&lt;none&gt;</c> when the variable is absent); <c>"set|{key}|{value}"</c> sets it;
    /// <c>"remove|{key}"</c> removes it; <c>"setparent|{key}|{value}"</c> sets it in the <c>ParentContext</c>;
    /// <c>"setandcreate|{key}|{value}|{entity}"</c> sets it, then creates a record of the entity (<c>pb_name</c> "forged").
    /// </summary>
    public class SharedVariablePlugin : IPlugin
    {
        private readonly string[] _parts;

        public SharedVariablePlugin(string unsecure, string secure) { _parts = unsecure.Split('|'); }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            switch (_parts[0])
            {
                case "record":
                    var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                    var service = factory.CreateOrganizationService(context.UserId);
                    var value = context.SharedVariables.Contains(_parts[2]) ? Convert.ToString(context.SharedVariables[_parts[2]], CultureInfo.InvariantCulture) : "<none>";
                    var e = new Entity(_parts[1]);
                    e["pb_name"] = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}", context.MessageName, context.Stage, context.Depth, value);
                    service.Create(e);
                    break;
                case "set":
                    context.SharedVariables[_parts[1]] = _parts[2];
                    break;
                case "remove":
                    context.SharedVariables.Remove(_parts[1]);
                    break;
                case "setandcreate":
                    context.SharedVariables[_parts[1]] = _parts[2];
                    var creator = ((IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory)))
                        .CreateOrganizationService(context.UserId);
                    creator.Create(new Entity(_parts[3]) { ["pb_name"] = "forged" });
                    break;
                case "setparent":
                    context.ParentContext.SharedVariables[_parts[1]] = _parts[2];
                    break;
                default:
                    throw new InvalidPluginExecutionException("Unknown mode " + _parts[0]);
            }
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
    /// S.2e3a, Task 4: config <c>"{log entity}"</c>, on a post-operation Create. Reads <c>OutputParameters["id"]</c> of its own view,
    /// then executes a Create of a <c>{log entity}</c> named <c>typed</c> and a RetrieveMultiple of the rows so named through its service,
    /// casting the answers to <c>CreateResponse</c> and <c>RetrieveMultipleResponse</c>, and creates a <c>{log entity}</c> named
    /// <c>typed-result</c> whose <c>pb_data</c> is <c>output={id is the primary id};created={id};rows={count};name={first row's name}</c>.
    /// </summary>
    public class TypedResponsesPlugin : IPlugin
    {
        private readonly string _log;

        public TypedResponsesPlugin(string unsecure, string secure) { _log = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            var output = context.OutputParameters.Contains("id") && context.OutputParameters["id"] is Guid id && id == context.PrimaryEntityId;
            var created = (CreateResponse)service.Execute(new CreateRequest { Target = new Entity(_log) { ["pb_name"] = "typed" } });
            var query = new QueryExpression(_log) { ColumnSet = new ColumnSet("pb_name") };
            query.Criteria.AddCondition("pb_name", ConditionOperator.Equal, "typed");
            var read = (RetrieveMultipleResponse)service.Execute(new RetrieveMultipleRequest { Query = query });
            var rows = read.EntityCollection.Entities;
            service.Create(new Entity(_log)
            {
                ["pb_name"] = "typed-result",
                ["pb_data"] = string.Format(CultureInfo.InvariantCulture, "output={0};created={1};rows={2};name={3}", output, created.id,
                    rows.Count, rows.Count > 0 ? rows[0].GetAttributeValue<string>("pb_name") : ""),
            });
        }
    }

    /// <summary>
    /// S.2e2c final review I1: a plug-in that executes a request on its own <c>InputParameters</c> collection. Config <c>"{log entity}"</c>.
    /// Before stage 30 at depth 1 it holds the collection, executes <c>new OrganizationRequest(MessageName) { Parameters = it }</c>,
    /// writes <c>same={held is the context's collection};key={held has AddedByNested};view={the context's has it}</c> into
    /// <c>Target["pb_data"]</c> and then adds <c>"Late"</c> to the held collection. At a deeper level before stage 30 it adds
    /// <c>"AddedByNested"</c>. After stage 30 at depth 1 it creates a <c>{log entity}</c> record named <c>post</c> whose
    /// <c>pb_data</c> is the sorted keys of its <c>InputParameters</c>.
    /// </summary>
    public class ForwardInputsPlugin : IPlugin
    {
        private readonly string _log;

        public ForwardInputsPlugin(string unsecure, string secure) { _log = unsecure; }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            if (context.Stage == 40)
            {
                if (context.Depth != 1) return;
                var keys = context.InputParameters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
                var log = new Entity(_log);
                log["pb_name"] = "post";
                log["pb_data"] = string.Join(",", keys);
                service.Create(log);
                return;
            }
            if (context.Depth > 1)
            {
                context.InputParameters["AddedByNested"] = 1;
                return;
            }
            var held = context.InputParameters;
            service.Execute(new OrganizationRequest(context.MessageName) { Parameters = held });
            ((Entity)held["Target"])["pb_data"] = string.Format(CultureInfo.InvariantCulture, "same={0};key={1};view={2}",
                ReferenceEquals(held, context.InputParameters), held.Contains("AddedByNested"),
                context.InputParameters.Contains("AddedByNested"));
            held["Late"] = 2;
        }
    }

    /// <summary>
    /// S.2e2c final review M1: puts one new <c>OptionSetValue(3)</c> into <c>OutputParameters["Choice"]</c> and into the attribute
    /// <c>c</c> of a new entity in <c>OutputParameters["Holder"]</c>, so the caller can check that the response holds one instance.
    /// </summary>
    public class OutputChoicePlugin : IPlugin
    {
        public OutputChoicePlugin(string unsecure, string secure) { }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var choice = new OptionSetValue(3);
            context.OutputParameters["Choice"] = choice;
            context.OutputParameters["Holder"] = new Entity("holder") { ["c"] = choice };
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
    /// S.2e3c1: records the access value a step sees. Configuration <c>"{entity}[|mask={int}]"</c>. A <c>PrincipalAccess</c> input
    /// creates a record of the entity named <c>PrincipalAccess|{CLR type}|{principal name}|{principal id}|{mask as int}</c> and, with
    /// <c>mask</c>, replaces the input with a new <c>PrincipalAccess</c> of the same principal and that mask.
    /// </summary>
    public class AccessValuesProbePlugin : IPlugin
    {
        private readonly string _entity;
        private readonly int? _mask;

        public AccessValuesProbePlugin(string unsecure, string secure)
        {
            var parts = unsecure.Split(new[] { '|' }, 2);
            _entity = parts[0];
            if (parts.Length > 1) _mask = int.Parse(parts[1].Substring("mask=".Length), CultureInfo.InvariantCulture);
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            if (context.InputParameters.Contains("PrincipalAccess") && context.InputParameters["PrincipalAccess"] is PrincipalAccess access)
            {
                var e = new Entity(_entity);
                e["pb_name"] = string.Format(CultureInfo.InvariantCulture, "PrincipalAccess|{0}|{1}|{2}|{3}", access.GetType().FullName,
                    access.Principal.LogicalName, access.Principal.Id, (int)access.AccessMask);
                service.Create(e);
                if (_mask.HasValue)
                    context.InputParameters["PrincipalAccess"] = new PrincipalAccess { Principal = access.Principal, AccessMask = (AccessRights)_mask.Value };
            }
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
    /// S.2e3c2a Task 5: with <c>CreateOrganizationService(context.UserId)</c>, adds the user of the config <c>"{user}|{template}"</c> to
    /// the access team of the step's record (<c>PrimaryEntityName</c>, <c>PrimaryEntityId</c>) for the template, with
    /// AddUserToRecordTeam. A fault propagates.
    /// </summary>
    public class AddUserToRecordTeamPlugin : IPlugin
    {
        private readonly Guid _user;
        private readonly Guid _template;

        public AddUserToRecordTeamPlugin(string unsecure, string secure)
        {
            var parts = unsecure.Split('|');
            _user = new Guid(parts[0]);
            _template = new Guid(parts[1]);
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            factory.CreateOrganizationService(context.UserId).Execute(new AddUserToRecordTeamRequest
            {
                Record = new EntityReference(context.PrimaryEntityName, context.PrimaryEntityId),
                SystemUserId = _user,
                TeamTemplateId = _template,
            });
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

#nullable disable
    /// <summary>
    /// S.2e2c golden dumps. Config <c>"{entity}"</c> or <c>"{entity}|{mutation}"</c>. The mutation (applied to <c>Target</c>, every
    /// <c>Targets</c> entity, <c>BusinessEntity</c> and every <c>BusinessEntityCollection</c> entity before the dump) is
    /// <c>attr=value</c> (sets a string), <c>attr=-</c> (removes the attribute), <c>attr=@option:N</c> (sets <c>Value</c> of the
    /// existing <c>OptionSetValue</c> in place) or <c>Target=replace:marker</c> (puts a new <c>Entity</c> of the same logical name and
    /// id, the copied attributes and <c>marker="replaced"</c> into <c>InputParameters["Target"]</c>). With entity <c>-</c> nothing
    /// is dumped (a mutation-only step). Otherwise it sets <c>SharedVariables["dump"]</c> to the stage and creates a record of
    /// <c>{entity}</c> named <c>Message|Stage|Mode|Depth</c>; its <c>pb_data</c> is <c>Seq: n</c> (the creation order in this
    /// process) and the canonical dump of every member of <c>IPluginExecutionContext</c> to <c>IPluginExecutionContext7</c> (see
    /// <see cref="ContextDumpWriter"/>). After the dump it appends the stage to <c>SharedVariables["dumped"]</c>.
    /// </summary>
    public class ContextDumpPlugin : IPlugin
    {
        private static int _sequence;

        private readonly string _entity;
        private readonly string _attribute;
        private readonly string _value;

        public ContextDumpPlugin(string unsecure, string secure)
        {
            var parts = unsecure.Split(new[] { '|' }, 2);
            _entity = parts[0];
            if (parts.Length > 1)
            {
                var i = parts[1].IndexOf('=');
                _attribute = parts[1].Substring(0, i);
                _value = parts[1].Substring(i + 1);
            }
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext7)serviceProvider.GetService(typeof(IPluginExecutionContext7));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            if (_attribute != null) Mutate(context);
            if (_entity == "-") return;
            var service = factory.CreateOrganizationService(context.UserId);
            context.SharedVariables["dump"] = context.Stage;

            var record = new Entity(_entity);
            record["pb_name"] = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}",
                context.MessageName, context.Stage, context.Mode, context.Depth);
            record["pb_data"] = "Seq: " + System.Threading.Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture)
                + "\n" + new ContextDumpWriter().Dump(context);
            service.Create(record);

            context.SharedVariables["dumped"] = (context.SharedVariables.Contains("dumped") ? context.SharedVariables["dumped"] + ";" : "")
                + context.Stage.ToString(CultureInfo.InvariantCulture);
        }

        private void Mutate(IPluginExecutionContext context)
        {
            if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity)
            {
                var target = (Entity)context.InputParameters["Target"];
                if (_attribute == "Target")
                {
                    var replacement = new Entity(target.LogicalName, target.Id);
                    foreach (var a in target.Attributes) replacement[a.Key] = a.Value;
                    replacement[_value.Substring("replace:".Length)] = "replaced";
                    context.InputParameters["Target"] = replacement;
                    return;
                }
                Apply(target);
            }
            if (context.InputParameters.Contains("Targets") && context.InputParameters["Targets"] is EntityCollection)
                foreach (var e in ((EntityCollection)context.InputParameters["Targets"]).Entities) Apply(e);
            if (context.OutputParameters.Contains("BusinessEntity") && context.OutputParameters["BusinessEntity"] is Entity)
                Apply((Entity)context.OutputParameters["BusinessEntity"]);
            if (context.OutputParameters.Contains("BusinessEntityCollection") && context.OutputParameters["BusinessEntityCollection"] is EntityCollection)
                foreach (var e in ((EntityCollection)context.OutputParameters["BusinessEntityCollection"]).Entities) Apply(e);
        }

        private void Apply(Entity e)
        {
            if (_value == "-") e.Attributes.Remove(_attribute);
            else if (_value.StartsWith("@option:", StringComparison.Ordinal))
                ((OptionSetValue)e[_attribute]).Value = int.Parse(_value.Substring("@option:".Length), CultureInfo.InvariantCulture);
            else e[_attribute] = _value;
        }
    }

    /// <summary>
    /// Writes the canonical dump of <see cref="ContextDumpPlugin"/>: the public properties of the context interfaces in ordinal
    /// name order, bags by ordinal key, values as <c>Type:value</c>. The known SDK types (entities, references, collections, option
    /// sets, money, aliased values, column sets, queries, relationships, managed properties) are written by explicit writers that
    /// name only their public data members; reflection over public properties is the fallback for any other type. Ids are written
    /// raw, dates as <c>{date}</c>, row versions as <c>{rowversion}</c>; the test names and numbers ids per scenario. Every
    /// reference object a plug-in can mutate (context, entity, reference, collection, bag, option set, money) is followed by an
    /// identity token <c>@obj{load}-{n}</c> from a process-wide table, so that the same instance has the same token in every dump of
    /// a scenario and a copy has a new one; the test renumbers the tokens per scenario.
    /// </summary>
    internal sealed class ContextDumpWriter
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> Tokens =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, string>();
        private static readonly string LoadId = Guid.NewGuid().ToString("N").Substring(0, 8);
        private static int _tokenCount;

        private readonly StringBuilder _out = new StringBuilder();

        public string Dump(IPluginExecutionContext7 context)
        {
            Line(0, "Context: " + Token(context));
            Members(1, context, 0);
            return _out.ToString().TrimEnd('\n');
        }

        private static string Token(object o)
        {
            return "@obj" + LoadId + "-" + Tokens.GetValue(o, _ => System.Threading.Interlocked.Increment(ref _tokenCount).ToString(CultureInfo.InvariantCulture));
        }

        private void Line(int indent, string text)
        {
            _out.Append(' ', indent * 2).Append(text).Append('\n');
        }

        private static IEnumerable<PropertyInfo> ContextProperties()
        {
            var types = new List<Type> { typeof(IPluginExecutionContext7) };
            types.AddRange(typeof(IPluginExecutionContext7).GetInterfaces());
            return types.SelectMany(t => t.GetProperties())
                .GroupBy(p => p.Name).Select(g => g.First()).OrderBy(p => p.Name, StringComparer.Ordinal);
        }

        private void Members(int indent, IPluginExecutionContext context, int depth)
        {
            foreach (var property in ContextProperties())
            {
                object value;
                try { value = property.GetValue(context, null); }
                catch (Exception ex) { Line(indent, property.Name + ": throws " + (ex.InnerException ?? ex).GetType().Name); continue; }
                Value(indent, property.Name, value, depth);
            }
        }

        private static string Scalar(object v)
        {
            if (v is string) return "\"" + ((string)v).Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
            if (v is Guid) return ((Guid)v).ToString("D");
            if (v is DateTime || v is DateTimeOffset) return "{date}";
            if (v is bool) return (bool)v ? "True" : "False";
            if (v is Enum) return v.ToString();
            if (v is byte[]) return BitConverter.ToString((byte[])v);
            if (v is IFormattable) return ((IFormattable)v).ToString(null, CultureInfo.InvariantCulture);
            if (v is Type) return ((Type)v).Name;
            return null;
        }

        private static string KeyText(object key)
        {
            var s = key as string;
            if (s != null) return s;
            var r = key as Relationship;
            if (r != null) return "Relationship:" + r.SchemaName + ":" + r.PrimaryEntityRole;
            return Convert.ToString(key, CultureInfo.InvariantCulture);
        }

        private static string TypeName(Type t)
        {
            var i = t.Name.IndexOf('`');
            return i < 0 ? t.Name : t.Name.Substring(0, i);
        }

        private void Value(int indent, string label, object v, int depth)
        {
            if (v == null) { Line(indent, label + ": null"); return; }
            if (depth > 16) { Line(indent, label + ": <depth limit>"); return; }
            if (label == "versionnumber" && v is long) { Line(indent, label + ": Int64:{rowversion}"); return; }
            var scalar = Scalar(v);
            if (scalar != null) { Line(indent, label + ": " + v.GetType().Name + ":" + scalar); return; }
            var d = depth + 1;
            var i = indent + 1;

            var parent = v as IPluginExecutionContext;
            if (parent != null) { Line(indent, label + ": Context " + Token(v)); Members(i, parent, d); return; }

            var entity = v as Entity;
            if (entity != null)
            {
                Line(indent, label + ": " + TypeName(v.GetType()) + "(" + entity.LogicalName + ") " + Token(v));
                Value(i, "Id", entity.Id, d);
                Value(i, "EntityState", entity.EntityState, d);
                Line(i, "RowVersion: " + (entity.RowVersion == null ? "null" : "{rowversion}"));
                Value(i, "Attributes", entity.Attributes, d);
                Value(i, "FormattedValues", entity.FormattedValues, d);
                Value(i, "KeyAttributes", entity.KeyAttributes, d);
                Value(i, "RelatedEntities", entity.RelatedEntities, d);
                return;
            }
            var reference = v as EntityReference;
            if (reference != null)
            {
                Line(indent, label + ": EntityReference " + Token(v));
                Value(i, "LogicalName", reference.LogicalName, d);
                Value(i, "Id", reference.Id, d);
                Value(i, "Name", reference.Name, d);
                Value(i, "KeyAttributes", reference.KeyAttributes, d);
                Line(i, "RowVersion: " + (reference.RowVersion == null ? "null" : "{rowversion}"));
                return;
            }
            var collection = v as EntityCollection;
            if (collection != null)
            {
                Line(indent, label + ": EntityCollection " + Token(v));
                Value(i, "EntityName", collection.EntityName, d);
                Value(i, "Entities", collection.Entities, d);
                Value(i, "MoreRecords", collection.MoreRecords, d);
                Value(i, "PagingCookie", collection.PagingCookie, d);
                Value(i, "TotalRecordCount", collection.TotalRecordCount, d);
                Value(i, "TotalRecordCountLimitExceeded", collection.TotalRecordCountLimitExceeded, d);
                return;
            }
            var option = v as OptionSetValue;
            if (option != null) { Line(indent, label + ": OptionSetValue " + Token(v)); Value(i, "Value", option.Value, d); return; }
            var money = v as Money;
            if (money != null) { Line(indent, label + ": Money " + Token(v)); Value(i, "Value", money.Value, d); return; }
            var aliased = v as AliasedValue;
            if (aliased != null)
            {
                Line(indent, label + ": AliasedValue " + Token(v));
                Value(i, "EntityLogicalName", aliased.EntityLogicalName, d);
                Value(i, "AttributeLogicalName", aliased.AttributeLogicalName, d);
                Value(i, "Value", aliased.Value, d);
                return;
            }
            var managed = v as BooleanManagedProperty;
            if (managed != null)
            {
                Line(indent, label + ": BooleanManagedProperty");
                Value(i, "Value", managed.Value, d);
                Value(i, "CanBeChanged", managed.CanBeChanged, d);
                Value(i, "ManagedPropertyLogicalName", managed.ManagedPropertyLogicalName, d);
                return;
            }
            var relationship = v as Relationship;
            if (relationship != null)
            {
                Line(indent, label + ": Relationship");
                Value(i, "SchemaName", relationship.SchemaName, d);
                Value(i, "PrimaryEntityRole", relationship.PrimaryEntityRole, d);
                return;
            }
            var columns = v as ColumnSet;
            if (columns != null)
            {
                Line(indent, label + ": ColumnSet");
                Value(i, "AllColumns", columns.AllColumns, d);
                Value(i, "Columns", columns.Columns, d);
                return;
            }
            var fetch = v as FetchExpression;
            if (fetch != null) { Line(indent, label + ": FetchExpression"); Value(i, "Query", fetch.Query, d); return; }
            var qe = v as QueryExpression;
            if (qe != null)
            {
                Line(indent, label + ": QueryExpression");
                Value(i, "EntityName", qe.EntityName, d);
                Value(i, "ColumnSet", qe.ColumnSet, d);
                Value(i, "Criteria", qe.Criteria, d);
                Orders(i, qe.Orders, d);
                Links(i, qe.LinkEntities, d);
                Value(i, "TopCount", qe.TopCount, d);
                Value(i, "Distinct", qe.Distinct, d);
                Value(i, "NoLock", qe.NoLock, d);
                Value(i, "PageInfo", qe.PageInfo, d);
                return;
            }
            var qba = v as QueryByAttribute;
            if (qba != null)
            {
                Line(indent, label + ": QueryByAttribute");
                Value(i, "EntityName", qba.EntityName, d);
                Value(i, "ColumnSet", qba.ColumnSet, d);
                Value(i, "Attributes", qba.Attributes, d);
                Value(i, "Values", qba.Values, d);
                Orders(i, qba.Orders, d);
                Value(i, "TopCount", qba.TopCount, d);
                Value(i, "PageInfo", qba.PageInfo, d);
                return;
            }
            var filter = v as FilterExpression;
            if (filter != null)
            {
                Line(indent, label + ": FilterExpression");
                Value(i, "FilterOperator", filter.FilterOperator, d);
                Line(i, "Conditions: [" + filter.Conditions.Count + "]");
                for (var k = 0; k < filter.Conditions.Count; k++)
                {
                    var c = filter.Conditions[k];
                    Line(i + 1, "[" + k + "]: ConditionExpression");
                    Value(i + 2, "EntityName", c.EntityName, d);
                    Value(i + 2, "AttributeName", c.AttributeName, d);
                    Value(i + 2, "Operator", c.Operator, d);
                    Value(i + 2, "Values", c.Values, d);
                }
                Line(i, "Filters: [" + filter.Filters.Count + "]");
                for (var k = 0; k < filter.Filters.Count; k++) Value(i + 1, "[" + k + "]", filter.Filters[k], d);
                return;
            }
            var paging = v as PagingInfo;
            if (paging != null)
            {
                Line(indent, label + ": PagingInfo");
                Value(i, "Count", paging.Count, d);
                Value(i, "PageNumber", paging.PageNumber, d);
                Value(i, "PagingCookie", paging.PagingCookie, d);
                Value(i, "ReturnTotalRecordCount", paging.ReturnTotalRecordCount, d);
                return;
            }

            var items = v as IEnumerable;
            if (items != null)
            {
                var keyed = new List<KeyValuePair<string, object>>();
                var listed = new List<object>();
                foreach (var item in items)
                {
                    var t = item == null ? null : item.GetType();
                    if (t != null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                        keyed.Add(new KeyValuePair<string, object>(KeyText(t.GetProperty("Key").GetValue(item, null)), t.GetProperty("Value").GetValue(item, null)));
                    else listed.Add(item);
                }
                Line(indent, label + ": " + TypeName(v.GetType()) + "[" + (keyed.Count + listed.Count) + "] " + Token(v));
                foreach (var pair in keyed.OrderBy(p => p.Key, StringComparer.Ordinal)) Value(i, pair.Key, pair.Value, d);
                for (var k = 0; k < listed.Count; k++) Value(i, "[" + k + "]", listed[k], d);
                return;
            }

            // Fallback for a type without an explicit writer: its public properties.
            var type = v.GetType();
            Line(indent, label + ": " + TypeName(type) + " " + Token(v));
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (property.GetIndexParameters().Length > 0 || property.Name == "ExtensionData" || !property.CanRead) continue;
                object value;
                try { value = property.GetValue(v, null); }
                catch (Exception ex) { Line(i, property.Name + ": throws " + (ex.InnerException ?? ex).GetType().Name); continue; }
                Value(i, property.Name, value, d);
            }
        }

        private void Orders(int indent, IEnumerable<OrderExpression> orders, int depth)
        {
            var list = orders.ToList();
            Line(indent, "Orders: [" + list.Count + "]");
            for (var k = 0; k < list.Count; k++)
            {
                Line(indent + 1, "[" + k + "]: OrderExpression");
                Value(indent + 2, "AttributeName", list[k].AttributeName, depth);
                Value(indent + 2, "OrderType", list[k].OrderType, depth);
            }
        }

        private void Links(int indent, IEnumerable<LinkEntity> links, int depth)
        {
            var list = links.ToList();
            Line(indent, "LinkEntities: [" + list.Count + "]");
            for (var k = 0; k < list.Count; k++)
            {
                var l = list[k];
                Line(indent + 1, "[" + k + "]: LinkEntity");
                var n = indent + 2;
                Value(n, "LinkFromEntityName", l.LinkFromEntityName, depth);
                Value(n, "LinkFromAttributeName", l.LinkFromAttributeName, depth);
                Value(n, "LinkToEntityName", l.LinkToEntityName, depth);
                Value(n, "LinkToAttributeName", l.LinkToAttributeName, depth);
                Value(n, "JoinOperator", l.JoinOperator, depth);
                Value(n, "EntityAlias", l.EntityAlias, depth);
                Value(n, "Columns", l.Columns, depth);
                Value(n, "LinkCriteria", l.LinkCriteria, depth);
                Orders(n, l.Orders, depth);
                Links(n, l.LinkEntities, depth);
            }
        }
    }

#nullable restore

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
