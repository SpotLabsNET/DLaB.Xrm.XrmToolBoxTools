﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DLaB.Common.Exceptions;
using DLaB.Xrm;
using DLaB.Xrm.Entities;
using McTools.Xrm.Connection;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace DLaB.AttributeManager
{
    public partial class Logic
    {
        public delegate void LogHandler(string text);
        public event LogHandler OnLog;

        public EntityMetadata Metadata { get; set; }
        public bool MigrateData { get; set; }
        public bool SupportsExecuteMultipleRequest { get; set; }
        public string TempPostfix { get; private set; }
        public IOrganizationService Service { get; private set; }
        public HashSet<int> ValidLanguageCodes { get; private set; }
        private const int Crm2013 = 6;
        private const int Crm2011 = 5;
        private const int Rollup12 = 3218;


        [Flags]
        public enum Steps
        {
            CreateTemp = 1,
            MigrateToTemp = 2,
            RemoveExistingAttribute = 4,
            CreateNewAttribute = 8,
            MigrateToNewAttribute = 16,
            RemoveTemp = 32,
            MigrationToTempRequired = 64
        }

        [Flags]
        public enum Action
        {
            Rename = 1,
            ChangeCase = 2,
            RemoveTemp = 4,
            ChangeType = 8,
            Delete = 16
        }

        public Logic(IOrganizationService service, ConnectionDetail connectionDetail, EntityMetadata metadata, string tempPostFix, bool migrateData)
        {
            SupportsExecuteMultipleRequest = connectionDetail.OrganizationMajorVersion >= Crm2013 ||
                                             (connectionDetail.OrganizationMajorVersion >= Crm2011 && int.Parse(connectionDetail.OrganizationVersion.Split('.')[3]) >= Rollup12);
            Service = service;
            TempPostfix = tempPostFix;
            MigrateData = migrateData;
            ValidLanguageCodes = GetValidLanguageCodes();
            Metadata = metadata;
        }

        private HashSet<int> GetValidLanguageCodes()
        {
            var resp = (RetrieveAvailableLanguagesResponse)Service.Execute(new RetrieveAvailableLanguagesRequest());
            return new HashSet<int>(resp.LocaleIds);
        }

        public void Run(AttributeMetadata att, string newAttributeSchemaName, Steps stepsToPerform, Action actions, AttributeMetadata newAttributeType = null)
        {
            var state = GetApplicationMigrationState(Service, att, newAttributeSchemaName);
            AssertValidStepsForState(att.SchemaName, newAttributeSchemaName, stepsToPerform, state, actions);
            var oldAtt = state.Old;
            var tmpAtt = state.Temp;
            var newAtt = state.New;

            switch (actions)
            {
                case Action.Delete:
                    ClearFieldDependencies(oldAtt);
                    RemoveExisting(stepsToPerform, oldAtt);
                    break;

                case Action.RemoveTemp:
                    RemoveTemp(stepsToPerform, tmpAtt);
                    break;

                case Action.Rename:
                case Action.Rename | Action.ChangeType:
                    CreateNew(newAttributeSchemaName, stepsToPerform, oldAtt, ref newAtt, newAttributeType); // Create or Retrieve the New Attribute
                    MigrateToNew(stepsToPerform, oldAtt, newAtt, actions);
                    RemoveExisting(stepsToPerform, oldAtt);
                    break;

                case Action.ChangeCase:
                case Action.ChangeCase | Action.ChangeType:
                case Action.ChangeType:
                    CreateTemp(stepsToPerform, oldAtt, ref tmpAtt, newAttributeType); // Either Create or Retrieve the Temp
                    MigrateToTemp(stepsToPerform, oldAtt, tmpAtt, actions);
                    RemoveExisting(stepsToPerform, oldAtt);
                    CreateNew(newAttributeSchemaName, stepsToPerform, tmpAtt, ref newAtt, newAttributeType);
                    MigrateToNew(stepsToPerform, tmpAtt, newAtt, actions);
                    RemoveTemp(stepsToPerform, tmpAtt);
                    break;
            }
        }

        private void ClearFieldDependencies(AttributeMetadata att)
        {
            Trace("Beginning Step: Clearing Field Dependencies");
            UpdateCharts(Service, att);
            UpdateViews(Service, att);
            UpdateForms(Service, att);
            UpdateRelationships(Service, att);
            UpdateMappings(Service, att);
            UpdateWorkflows(Service, att);
            PublishEntity(Service, att.EntityLogicalName);
            AssertCanDelete(Service, att);
            Trace("Completed Step: Clearing Field Dependencies" + Environment.NewLine);
        }

        private void CreateTemp(Steps stepsToPerform, AttributeMetadata oldAtt, ref AttributeMetadata tmpAtt, AttributeMetadata newAttributeType)
        {
            if (stepsToPerform.HasFlag(Steps.CreateTemp))
            {
                Trace("Beginning Step: Create Temp");
                tmpAtt = tmpAtt ?? CreateAttributeWithDifferentName(Service, oldAtt, oldAtt.SchemaName + TempPostfix, newAttributeType);
                Trace("Completed Step: Create Temp" + Environment.NewLine);
            }
        }

        private void RemoveTemp(Steps stepsToPerform, AttributeMetadata tmpAtt)
        {
            if (stepsToPerform.HasFlag(Steps.RemoveTemp))
            {
                Trace("Beginning Step: Remove Temporary Field");
                DeleteField(Service, tmpAtt);
                Trace("Completed Step: Remove Temporary Field" + Environment.NewLine);
            }
        }

        private void MigrateToNew(Steps stepsToPerform, AttributeMetadata tmpAtt, AttributeMetadata newAtt, Action actions)
        {
            if (stepsToPerform.HasFlag(Steps.MigrateToNewAttribute))
            {
                Trace("Beginning Step: Migrate To New Attribute");
                MigrateAttribute(tmpAtt, newAtt, actions);
                Trace("Completed Step: Migrate To New Attribute" + Environment.NewLine);
            }
        }

        private void CreateNew(string newAttributeSchemaName, Steps stepsToPerform, AttributeMetadata attributeToCopy, ref AttributeMetadata createdAttributeOrAttributeIfAlreadyCreated, AttributeMetadata newAttributeType)
        {
            if (stepsToPerform.HasFlag(Steps.CreateNewAttribute))
            {
                Trace("Beginning Step: Create New Attribute");
                createdAttributeOrAttributeIfAlreadyCreated = createdAttributeOrAttributeIfAlreadyCreated ?? CreateAttributeWithDifferentName(Service, attributeToCopy, newAttributeSchemaName, newAttributeType);
                Trace("Completed Step: Create New Attribute" + Environment.NewLine);
            }
        }

        private void RemoveExisting(Steps stepsToPerform, AttributeMetadata oldAtt)
        {
            if (stepsToPerform.HasFlag(Steps.RemoveExistingAttribute))
            {
                Trace("Beginning Step: Remove Existing Attribute");
                DeleteField(Service, oldAtt);
                Trace("Completed Step: Remove Existing Attribute" + Environment.NewLine);
            }
        }

        private void MigrateToTemp(Steps stepsToPerform, AttributeMetadata oldAtt, AttributeMetadata tmpAtt, Action actions)
        {
            if (stepsToPerform.HasFlag(Steps.MigrateToTemp))
            {
                Trace("Beginning Step: Migrate To Temp");
                MigrateAttribute(oldAtt, tmpAtt, actions);
                Trace("Completed Step: Migrate To Temp" + Environment.NewLine);
            }
        }

        private void MigrateAttribute(AttributeMetadata fromAtt, AttributeMetadata toAtt, Action actions)
        {
            // Replace Old Attribute with Tmp Attribute
            CopyData(Service, fromAtt, toAtt, actions);
            UpdateCalculatedFields(Service, fromAtt, toAtt);
            UpdateCharts(Service, fromAtt, toAtt);
            UpdateViews(Service, fromAtt, toAtt);
            UpdateForms(Service, fromAtt, toAtt);
            UpdateWorkflows(Service, fromAtt, toAtt);
            UpdatePluginStepFilters(Service, fromAtt, toAtt);
            UpdatePluginStepImages(Service, fromAtt, toAtt);
            PublishEntity(Service, fromAtt.EntityLogicalName);
            AssertCanDelete(Service, fromAtt);
        }

        private AttributeMigrationState GetApplicationMigrationState(IOrganizationService service, AttributeMetadata att, string newSchemaName)
        {
            var entityName = att.EntityLogicalName;
            var schemaName = att.SchemaName;
            var tempSchemaName = att.SchemaName + TempPostfix;
            var state = new AttributeMigrationState();

            var metadata = ((RetrieveEntityResponse) service.Execute(new RetrieveEntityRequest {LogicalName = att.EntityLogicalName, EntityFilters = EntityFilters.Attributes})).EntityMetadata;
            
            Trace("Searching for Existing Attribute " + entityName + "." + schemaName);
            state.Old = metadata.Attributes.FirstOrDefault(a => a.SchemaName == schemaName);
            Trace("Existing Attribute {0}.{1} {2}found", entityName, schemaName, state.Old == null ? "not " : string.Empty);

            Trace("Searching for Temp Attribute " + entityName + "." + tempSchemaName);
            state.Temp = metadata.Attributes.FirstOrDefault(a => a.SchemaName == tempSchemaName);
            Trace("Temp Attribute {0}.{1} {2}found", entityName, tempSchemaName, state.Temp == null ? "not " : string.Empty);

            Trace("Searching for New Attribute " + entityName + "." + newSchemaName);
            state.New = metadata.Attributes.FirstOrDefault(a => a.SchemaName == newSchemaName);
            Trace("New Attribute {0}.{1} {2}found", entityName, newSchemaName, state.New == null ? "not " : string.Empty);

            return state;
        }

        private void AssertValidStepsForState(string existingSchemaName, string newSchemaName, Steps stepsToPerform, AttributeMigrationState state, Action actions)
        {
            // TODO CLEAN THIS UP!
            if (actions.HasFlag(Action.ChangeType) && existingSchemaName == newSchemaName && state.Old != null && state.New != null)
            {
                Trace("Only an attribute type change has been requested.  Attempting to differentiate between existing and new.");

                if (state.Temp == null)
                {
                    Trace("No Temporary Attribute was found.  Treating New as not yet created.");
                    state.New = null;
                }
                else if (state.Old.GetType() == state.Temp.GetType())
                {
                    if (stepsToPerform.HasFlag(Steps.RemoveExistingAttribute) || stepsToPerform.HasFlag(Steps.CreateNewAttribute) || stepsToPerform.HasFlag(Steps.MigrateToTemp))
                    {
                        Trace("A Temporary Attribute was found and a request has been made to either remove the existing attribute, create a new attribute, or migrate to temp.  Treating New as not yet created.");
                        state.New = null;
                    }
                    else
                    {
                        Trace("A Temporary Attribute was found and a request has not been made to either remove the existing attribute, create a new attribute, or migrate to temp.  Treating New as already created.");
                        state.Old = null;
                    }
                }
                else
                {
                    Trace("A Temporary Attribute was found and the current Attribute Type is different.  Treating New as not yet created.");
                    state.New = null;
                }
            }

            Trace("Validating Current CRM State Before Performing Steps:");

            if (stepsToPerform.HasFlag(Steps.CreateTemp) && state.Temp != null)
            {
                throw new InvalidOperationException("Unable to Create Temp!  Temp " + state.Temp.EntityLogicalName + "." + state.Temp.LogicalName + " already exists!");
            }

            if (stepsToPerform.HasFlag(Steps.MigrateToTemp))
            {
                // Can only Migrate if old already exists
                if (state.Old == null)
                {
                    throw new InvalidOperationException("Unable to Migrate!  Existing Attribute " + existingSchemaName + " does not exist!");
                }

                // Can only Migrate if Tmp already exists, or temp will be created
                if (!(state.Temp != null || stepsToPerform.HasFlag(Steps.CreateTemp)))
                {
                    throw new InvalidOperationException("Unable to Migrate!  Temporary Attribute " + existingSchemaName + TempPostfix + " does not exist!");
                }
            }


            if (stepsToPerform.HasFlag(Steps.RemoveExistingAttribute))
            {
                if (state.Old == null)
                {
                    AssertInvalidState("Unable to Remove Existing Attribute! Attribute " + existingSchemaName + " does not exist!");
                }

                // Can only Remove existing if Tmp already exists, or temp will be created, or action is to delete, or if performing rename and there is a Create Or the New Already exists
                if (!(
                        state.Temp != null 
                        || stepsToPerform.HasFlag(Steps.CreateTemp) 
                        || actions.HasFlag(Action.Delete)
                        || (!string.Equals(existingSchemaName, newSchemaName, StringComparison.OrdinalIgnoreCase) 
                            && (stepsToPerform.HasFlag(Steps.CreateNewAttribute) || state.New != null))))
                {
                    AssertInvalidState("Unable to Remove Existing Attribute!  Temporary Attribute " + existingSchemaName + TempPostfix + " does not exist!");
                }

                // Can only Remove existing if Tmp will be migrated, or has been migrated, or action is delete
                if (!(
                        (actions.HasFlag(Action.ChangeCase) && stepsToPerform.HasFlag(Steps.MigrateToTemp)) 
                        || (actions.HasFlag(Action.Rename) && stepsToPerform.HasFlag(Steps.MigrateToNewAttribute))
                        || actions.HasFlag(Action.Delete)))
                {
                    try
                    {
                        AssertCanDelete(Service, state.Old);
                    }
                    catch
                    {
                        AssertInvalidState("Unable to Remove Existing!  Existing Attribute " + existingSchemaName + " has not been migrated to Temporary Attribute!");
                    }
                }
            }

            if (stepsToPerform.HasFlag(Steps.CreateNewAttribute))
            {
                if (stepsToPerform.HasFlag(Steps.MigrationToTempRequired))
                {
                    // Temp is required, Can only Create New, if Old doesn't exist, or will be removed
                    if(!(state.Old == null || stepsToPerform.HasFlag(Steps.RemoveExistingAttribute)))
                    {
                        AssertInvalidState("Unable to create new Attribute!  Old Attribute " + existingSchemaName + " still exists!");
                    }
                }


                // Can only Create Global if doesn't already exist
                if (state.New != null)
                {
                    AssertInvalidState("Unable to create new Attribute!  New Attribute " + existingSchemaName + " already exists!");
                }
            }

            if (stepsToPerform.HasFlag(Steps.MigrateToNewAttribute))
            {
                // Can only Migrate To New if Temp Exists, or Creating a Temp, or There is a Rename and the Old Already Exists
                if (!(state.Temp != null || stepsToPerform.HasFlag(Steps.CreateTemp) || (actions.HasFlag(Action.Rename) && state.Old != null)))
                {
                    AssertInvalidState("Unable to Migrate!  Temp Attribute " + existingSchemaName + TempPostfix + " does not exist!");
                }

                // Can only Migrate if New Already exists, or New will be created
                if (!(state.New != null || stepsToPerform.HasFlag(Steps.CreateNewAttribute)))
                {
                    AssertInvalidState("Unable to Migrate!  New Attribute " + existingSchemaName + " does not exist!");
                }
            }

            if (stepsToPerform.HasFlag(Steps.RemoveTemp))
            {
                // Can Only remove Temp if it exists, or will exist
                if (!(state.Temp != null || stepsToPerform.HasFlag(Steps.CreateTemp)))
                {
                    AssertInvalidState("Unable to Remove Temp!  Temp Attribute " + existingSchemaName + TempPostfix + " does not exist!");
                }

                // Can Only remove Temp if new Attribute Already exists, or if only step is RemoveTemp 
                if (!(state.New != null || stepsToPerform.HasFlag(Steps.CreateNewAttribute) || stepsToPerform == Steps.RemoveTemp))
                {
                    AssertInvalidState("Unable to Migrate!  New Attribute " + existingSchemaName + " does not exist!");
                }

                // Can only Remove tmp if global will be migrated, or has been migrated
                if (!stepsToPerform.HasFlag(Steps.MigrateToNewAttribute))
                {
                    try
                    {
                        AssertCanDelete(Service, state.Temp);
                    }
                    catch
                    {
                        AssertInvalidState("Unable to Remove Old Attribute!  Old Attribute " + existingSchemaName + " has not been migrated to Temporary Attribute!");
                    }
                }
            }
        }

        private void AssertInvalidState(string message)
        {
            throw new InvalidOperationException(message);
        }

        private void DeleteField(IOrganizationService service, AttributeMetadata att)
        {
            Trace("Deleting Field " + att.EntityLogicalName + "." + att.LogicalName);
            service.Execute(new DeleteAttributeRequest
            {
                EntityLogicalName = att.EntityLogicalName,
                LogicalName = att.LogicalName
            });
        }

        private void AssertCanDelete(IOrganizationService service, AttributeMetadata attribute)
        {
            Trace("Checking for Delete Dependencies for " + attribute.EntityLogicalName + "." + attribute.LogicalName);
            var depends = (RetrieveDependenciesForDeleteResponse)service.Execute(new RetrieveDependenciesForDeleteRequest
            {
                ComponentType = (int)ComponentType.Attribute,
                ObjectId = attribute.MetadataId.GetValueOrDefault()
            });

            var errors = new List<string>();
            foreach (var d in depends.EntityCollection.ToEntityList<Dependency>())
            {
                var type = (ComponentType)d.DependentComponentType.GetValueOrDefault();
                var dependentId = d.DependentComponentObjectId.GetValueOrDefault();
                var err = type + " " + dependentId;
                switch (type) {
                    case ComponentType.Attribute:
                        var req = new RetrieveAttributeRequest
                        {
                            MetadataId = dependentId
                        };
                        var dependent = ((RetrieveAttributeResponse)service.Execute(req)).AttributeMetadata;
                       
                        err = $"{err} ({dependent.EntityLogicalName + " : " + dependent.DisplayName.GetLocalOrDefaultText()}";
                        break;

                    case ComponentType.EntityRelationship:
                        var response =
                            (RetrieveRelationshipResponse)service.Execute(new RetrieveRelationshipRequest { MetadataId = dependentId });
                        Trace("Entity Relationship / Mapping {0} must be manually removed/added", response.RelationshipMetadata.SchemaName);

                        break;

                    case ComponentType.SavedQueryVisualization:
                        var sqv = service.GetEntity<SavedQueryVisualization>(dependentId);
                            err = $"{err} ({sqv.Name} - {sqv.CreatedBy.Name})";
                        break;

                    case ComponentType.SavedQuery:
                        var sq = service.GetEntity<SavedQuery>(dependentId);
                            err = $"{err} ({sq.Name} - {sq.CreatedBy.Name})";
                        break;

                    case ComponentType.Workflow:
                        var workflow = service.GetEntity<Workflow>(d.DependentComponentObjectId.GetValueOrDefault());
                        err = err + " " + workflow.Name + " (" + workflow.CategoryEnum + ")";
                            break;
                }

                errors.Add(err);
            }

            if (errors.Count > 0)
            {
                throw new Exception("Dependencies found: " + Environment.NewLine + "\t" + string.Join(Environment.NewLine + "\t", errors));
            }
        }

        private void UpdateCalculatedFields(IOrganizationService service, AttributeMetadata from, AttributeMetadata to)
        {
            Trace("Checking for Calculated Field Dependencies");
            var depends = ((RetrieveDependenciesForDeleteResponse)service.Execute(new RetrieveDependenciesForDeleteRequest
            {
                ComponentType = (int)ComponentType.Attribute,
                ObjectId = from.MetadataId.GetValueOrDefault()
            })).EntityCollection.ToEntityList<Dependency>().Where(d => d.DependentComponentTypeEnum == ComponentType.Attribute).ToList();

            if (!depends.Any())
            {
                Trace("No Calculated Dependencies Found");
                return;
            }

            foreach (var dependency in depends)
            {
                Trace($"Retrieving Dependent Attribute {dependency.DependentComponentObjectId}");
                var att = ((RetrieveAttributeResponse) Service.Execute(new RetrieveAttributeRequest
                {
                    MetadataId = dependency.DependentComponentObjectId.GetValueOrDefault()
                })).AttributeMetadata;
                Trace($"Updating Dependent Attribute: {att.DisplayName.GetLocalOrDefaultText()}");

                var response = UpdateFormulaDefintionLogic.Update(att, from, to);
                if (!response.HasFormula)
                {
                    Trace("Dependency does not have a Formula Definition.  Unable to Remove Dependency.");
                }

                Trace($"Updating FormulatDefinition from {response.CurrentForumla} to {response.NewFormula}.");
                Service.Execute(new UpdateAttributeRequest
                {
                    Attribute = att,
                    EntityName = att.EntityLogicalName
                });
                Trace($"Successfully Removed Dependency for Dependent Attribute: {att.DisplayName.GetLocalOrDefaultText()}");
            }

           
        }

        private void UpdateCharts(IOrganizationService service, AttributeMetadata from, AttributeMetadata to)
        {
            foreach (var chart in GetSystemChartsWithAttribute(service, from))
            {
                Trace("Updating Chart " + chart.Name);
                chart.DataDescription = ReplaceFetchXmlAttribute(chart.DataDescription, from.LogicalName, to.LogicalName);

                service.Update(chart);
            }

            foreach (var chart in GetUserChartsWithAttribute(service, from))
            {
                Trace("Updating Chart " + chart.Name);
                chart.DataDescription = ReplaceFetchXmlAttribute(chart.DataDescription, from.LogicalName, to.LogicalName);
                service.Update(chart);
            }
        }

        private List<SavedQueryVisualization> GetSystemChartsWithAttribute(IOrganizationService service, AttributeMetadata from)
        {
            var qe = QueryExpressionFactory.Create<SavedQueryVisualization>(q => new
            {
                q.Id,
                q.DataDescription
            });

            AddFetchXmlCriteria(qe, SavedQueryVisualization.Fields.DataDescription, from.EntityLogicalName, from.LogicalName);

            Trace("Retrieving System Charts with Query: " + qe.GetSqlStatement());
            return service.GetEntities(qe);
        }

        private List<UserQueryVisualization> GetUserChartsWithAttribute(IOrganizationService service, AttributeMetadata from)
        {
            var qe = QueryExpressionFactory.Create<UserQueryVisualization>(q => new
            {
                q.Id,
                q.DataDescription
            });

            AddFetchXmlCriteria(qe, UserQueryVisualization.Fields.DataDescription, from.EntityLogicalName, from.LogicalName);

            Trace("Retrieving System Charts with Query: " + qe.GetSqlStatement());
            return service.GetEntities(qe);
        }

        private List<SystemForm> GetFormsWithAttribute(IOrganizationService service, AttributeMetadata att)
        {
            var qe = QueryExpressionFactory.Create<SystemForm>(q => new
            {
                q.Id,
                q.Name,
                q.FormXml
            },
                SystemForm.Fields.ObjectTypeCode, att.EntityLogicalName,
                new ConditionExpression("formxml", ConditionOperator.Like, "%<control %datafieldname=\"" + att.LogicalName + "\"%"));

            Trace("Retrieving Forms with Query: " + qe.GetSqlStatement());
            return service.GetEntities(qe);
        }

        private void UpdateForms(IOrganizationService service, AttributeMetadata from, AttributeMetadata to)
        {
            /*
             * <row>
             *   <cell id="{056d159e-9144-d809-378b-9e04a7626953}" showlabel="true" locklevel="0">
             *     <labels>
             *       <label description="Points" languagecode="1033" />
             *     </labels>
             *     <control id="new_points" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="new_points" disabled="true" />
             *   </cell>
             *   <cell id="{056d159e-9144-d809-378b-9e04a7626953}" showlabel="true" locklevel="0">
             *     <labels>
             *       <label description="Points" languagecode="1033" />
             *     </labels>
             *     <control id="header_new_points" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="new_points" disabled="true" />
             *   </cell>
             * </row>
             */

            foreach (var form in GetFormsWithAttribute(service, from))
            {
                Trace("Updating Form " + form.Name);
                var fromDataFieldStart = "datafieldname=\"" + from.LogicalName + "\"";
                var fromControlStart = "<control id=\"";
                const string classIdStart = "classid=\"{";
                var xml = form.FormXml;
                var dataFieldIndex = xml.IndexOf(fromDataFieldStart, StringComparison.OrdinalIgnoreCase);
                if (dataFieldIndex < 0)
                {
                    break;
                }
                var index = xml.LastIndexOf(fromControlStart, dataFieldIndex, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    index = xml.IndexOf(classIdStart, index, StringComparison.OrdinalIgnoreCase) + classIdStart.Length;
                    var classIdEnd = xml.IndexOf("}",index, StringComparison.OrdinalIgnoreCase);
                    xml = xml.Remove(index, classIdEnd - index).
                                                Insert(index, GetClassId(to));

                    dataFieldIndex = xml.IndexOf(fromDataFieldStart, ++dataFieldIndex, StringComparison.OrdinalIgnoreCase);
                    if (dataFieldIndex < 0)
                    {
                        break;
                    }
                    index = xml.LastIndexOf(fromControlStart, dataFieldIndex, StringComparison.OrdinalIgnoreCase);
                }
                form.FormXml = xml.Replace(fromControlStart + from.LogicalName + "\"", fromControlStart + to.LogicalName + "\"")
                                  .Replace(fromDataFieldStart, "datafieldname=\"" + to.LogicalName + "\"");
                service.Update(form);
            }
        }

        private string GetClassId(AttributeMetadata att)
        {
            switch (att.AttributeType.GetValueOrDefault(AttributeTypeCode.String))
            {
                case AttributeTypeCode.Boolean:
                    return "B0C6723A-8503-4FD7-BB28-C8A06AC933C2"; // CheckBoxControl
                case AttributeTypeCode.DateTime:
                    return "5B773807-9FB2-42DB-97C3-7A91EFF8ADFF"; // DateTimeControl
                case AttributeTypeCode.Decimal:
                    return "C3EFE0C3-0EC6-42BE-8349-CBD9079DFD8E"; // DecimalControl
                case AttributeTypeCode.Double:
                    return "0D2C745A-E5A8-4C8F-BA63-C6D3BB604660"; // FloatControl
                case AttributeTypeCode.Integer:
                    return "C6D124CA-7EDA-4A60-AEA9-7FB8D318B68F"; // IntegerControl 
                case AttributeTypeCode.Lookup:
                    return "270BD3DB-D9AF-4782-9025-509E298DEC0A"; // LookupControl
                case AttributeTypeCode.Money:
                    return "533B9E00-756B-4312-95A0-DC888637AC78"; // MoneyControl
                case AttributeTypeCode.PartyList:
                    return "CBFB742C-14E7-4A17-96BB-1A13F7F64AA2"; // PartyListControl
                case AttributeTypeCode.Picklist:
                    return "3EF39988-22BB-4F0B-BBBE-64B5A3748AEE"; // PickListControl
                case AttributeTypeCode.Status:
                    return "5D68B988-0661-4DB2-BC3E-17598AD3BE6C"; // PicklistStatusControl
                case AttributeTypeCode.String:
                    var format = ((StringAttributeMetadata) att).Format.GetValueOrDefault();
                    switch (format)
                    {
                        case StringFormat.Email:
                            return "ADA2203E-B4CD-49BE-9DDF-234642B43B52"; // EmailAddressControl
                        case StringFormat.Text:
                            return "4273EDBD-AC1D-40D3-9FB2-095C621B552D"; // TextBoxControl
                        case StringFormat.TextArea:
                            return "E0DECE4B-6FC8-4A8F-A065-082708572369"; // TextAreaControl
                        case StringFormat.Url:
                            return "71716B6C-711E-476C-8AB8-5D11542BFB47"; // UrlControl
                        case StringFormat.TickerSymbol:
                            return "1E1FC551-F7A8-43AF-AC34-A8DC35C7B6D4"; // TickerControl
                        case StringFormat.Phone:
                            return "8C10015A-B339-4982-9474-A95FE05631A5"; // PhoneNumberControl
                        case StringFormat.PhoneticGuide:
                        case StringFormat.VersionNumber:
                            throw new NotImplementedException("Unable to determine the Control ClassId for StringAttribute.Formt of " + format);
                        default:
                            throw new EnumCaseUndefinedException<StringFormat>(format);
                    }
                case AttributeTypeCode.BigInt:
                    return "C6D124CA-7EDA-4A60-AEA9-7FB8D318B68F"; // IntegerControl
                case AttributeTypeCode.EntityName:
                case AttributeTypeCode.Virtual:
                case AttributeTypeCode.CalendarRules:
                case AttributeTypeCode.Customer:
                case AttributeTypeCode.ManagedProperty:
                case AttributeTypeCode.Memo:
                case AttributeTypeCode.Owner:
                case AttributeTypeCode.State:
                case AttributeTypeCode.Uniqueidentifier:
                    throw new NotImplementedException("Unable to determine the Control ClassId for AttributeTypeCode." + att.AttributeType);
                default:
                    throw new EnumCaseUndefinedException<AttributeTypeCode>(att.AttributeType.GetValueOrDefault(AttributeTypeCode.String));
            }
        }

        private void UpdatePluginStepFilters(IOrganizationService service, AttributeMetadata att, AttributeMetadata to)
        {
            var qe = QueryExpressionFactory.Create<SdkMessageProcessingStep>();
            AddConditionsForValueInCsv(qe.Criteria, qe.EntityName, SdkMessageProcessingStep.Fields.FilteringAttributes, att.LogicalName);

            Trace("Checking for Plugin Registration Step Filtering Attribute Dependencies with Query: " + qe.GetSqlStatement());

            foreach (var step in service.GetEntities(qe))
            {
                var filter = ReplaceCsvValues(step.FilteringAttributes, att.LogicalName, to.LogicalName);
                Trace("Updating {0} - \"{1}\" to \"{2}\"", step.Name, step.FilteringAttributes, filter);
                service.Update(new SdkMessageProcessingStep()
                {
                    Id = step.Id,
                    FilteringAttributes = filter
                });
            }
        }

        private static void AddConditionsForValueInCsv(FilterExpression filter, string entityName, string fieldName, string value)
        {
            filter.WhereEqual(entityName,
                new ConditionExpression(fieldName, ConditionOperator.Like, $"%,{value},%"),
                LogicalOperator.Or,
                new ConditionExpression(fieldName, ConditionOperator.Like, $"{value},%"),
                LogicalOperator.Or,
                new ConditionExpression(fieldName, ConditionOperator.Like, $"%,{value}"),
                LogicalOperator.Or,
                fieldName, "{0}"
                );
        }

        private static string ReplaceCsvValues(string value, string from, string to)
        {
            var values = value.Split(',');
            var index = Array.IndexOf(values, from);
            while (index >= 0)
            {
                values[index] = to;
                index = Array.IndexOf(values, from);
            }
            return string.Join(",", values);
        }

        private void UpdatePluginStepImages(IOrganizationService service, AttributeMetadata att, AttributeMetadata to)
        {
            var qe = QueryExpressionFactory.Create<SdkMessageProcessingStepImage>();
            AddConditionsForValueInCsv(qe.Criteria, qe.EntityName, SdkMessageProcessingStepImage.Fields.Attributes1, att.LogicalName);

            Trace("Checking for Plugin Registration Step Filtering Attribute Dependencies with Query: " + qe.GetSqlStatement());

            foreach (var step in service.GetEntities(qe))
            {
                var filter = ReplaceCsvValues(step.Attributes1, att.LogicalName, to.LogicalName);
                Trace("Updating {0} - \"{1}\" to \"{2}\"", step.Name, step.Attributes1, filter);
                service.Update(new SdkMessageProcessingStepImage
                {
                    Id = step.Id,
                    Attributes1 = filter
                });
            }
        }

        private void UpdateWorkflows(IOrganizationService service, AttributeMetadata att, AttributeMetadata to)
        {
            Trace("Checking for Workflow Dependencies");
            var depends = ((RetrieveDependenciesForDeleteResponse) service.Execute(new RetrieveDependenciesForDeleteRequest
            {
                ComponentType = (int) ComponentType.Attribute,
                ObjectId = att.MetadataId.GetValueOrDefault()
            })).EntityCollection.ToEntityList<Dependency>().Where(d => d.DependentComponentTypeEnum == ComponentType.Workflow).ToList();

            if (!depends.Any())
            {
                Trace("No Workflow Dependencies Found");
                return;
            }

            foreach (var workflow in service.GetEntitiesById<Workflow>(depends.Select(d => d.DependentComponentObjectId.GetValueOrDefault())))
            {
                Trace("Updating {0} - {1} ({2})", workflow.CategoryEnum.ToString(), workflow.Name, workflow.Id);
                var xml = UpdateBusinessProcessFlowClassId(workflow.Xaml, att, to);
                workflow.Xaml = xml.Replace("\"" + att.LogicalName + "\"", "\"" + to.LogicalName + "\"");
                var activate = workflow.StateCode == WorkflowState.Activated;
                if (activate)
                {
                    service.Execute(new SetStateRequest()
                    {
                        EntityMoniker = workflow.ToEntityReference(),
                        State = new OptionSetValue((int) WorkflowState.Draft),
                        Status = new OptionSetValue((int) Workflow_StatusCode.Draft)
                    });
                }

                try
                {
                    var triggers = service.GetEntities<ProcessTrigger>(ProcessTrigger.Fields.ProcessId,
                        workflow.Id,
                        ProcessTrigger.Fields.ControlName,
                        att.LogicalName);

                    foreach (var trigger in triggers)
                    {
                        Trace("Updating Trigger {0} for Workflow", trigger.Id);
                        service.Update(new ProcessTrigger
                        {
                            Id = trigger.Id,
                            ControlName = to.LogicalName
                        });
                    }

                    service.Update(workflow);
                }
                finally
                {
                    if (activate)
                    {
                        service.Execute(new SetStateRequest()
                        {
                            EntityMoniker = workflow.ToEntityReference(),
                            State = new OptionSetValue((int) WorkflowState.Activated),
                            Status = new OptionSetValue((int) Workflow_StatusCode.Activated)
                        });
                    }
                }
            }
        }

        private string UpdateBusinessProcessFlowClassId(string xml, AttributeMetadata att, AttributeMetadata to)
        {
            var fromDataFieldStart = "DataFieldName=\"" + att.LogicalName + "\"";
            var fromControlStart = "<mcwb:Control ";
            const string classIdStart = "ClassId=\"";
            var dataFieldIndex = xml.IndexOf(fromDataFieldStart, StringComparison.OrdinalIgnoreCase);
            if (dataFieldIndex < 0)
            {
                return xml;
            }
            var index = xml.LastIndexOf(fromControlStart, dataFieldIndex, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                index = xml.IndexOf(classIdStart, index, StringComparison.OrdinalIgnoreCase) + classIdStart.Length;
                var classIdEnd = xml.IndexOf("\" ", index, StringComparison.OrdinalIgnoreCase);
                xml = xml.Remove(index, classIdEnd - index).
                          Insert(index, GetClassId(to));

                dataFieldIndex = xml.IndexOf(fromDataFieldStart, ++dataFieldIndex, StringComparison.OrdinalIgnoreCase);
                if (dataFieldIndex < 0)
                {
                    break;
                }
                index = xml.LastIndexOf(fromControlStart, dataFieldIndex, StringComparison.OrdinalIgnoreCase);
            }
            return xml;
        }

        private void UpdateViews(IOrganizationService service, AttributeMetadata from, AttributeMetadata to)
        {
            foreach (var query in GetViewsWithAttribute(service, from))
            {
                Trace("Updating View " + query.Name);
                query.FetchXml = ReplaceFetchXmlAttribute(query.FetchXml, from.LogicalName, to.LogicalName);

                if (query.LayoutXml != null)
                {
                    query.LayoutXml = ReplaceFetchXmlAttribute(query.LayoutXml, from.LogicalName, to.LogicalName, true);
                }
                service.Update(query);
            }
        }

        private List<SavedQuery> GetViewsWithAttribute(IOrganizationService service, AttributeMetadata @from)
        {
            var qe = QueryExpressionFactory.Create<SavedQuery>(q => new
            {
                q.Id,
                q.Name,
                q.QueryType,
                q.FetchXml,
                q.LayoutXml
            });

            AddFetchXmlCriteria(qe, SavedQuery.Fields.FetchXml, @from.EntityLogicalName, @from.LogicalName);

            Trace("Retrieving Views with Query: " + qe.GetSqlStatement());
            var views = service.GetEntities(qe);
            return views;
        }

        private string ReplaceFetchXmlAttribute(string xml, string from, string to, bool nameOnly = false)
        {
            xml = xml.Replace($"name=\"{from}\"", $"name=\"{to}\"");
            if (nameOnly)
            {
                return xml;
            }
            return xml.Replace($"attribute=\"{from}\"", $"attribute=\"{to}\"");
        }

        private static void AddFetchXmlCriteria(QueryExpression qe, string fieldName, string entityName, string attributeName)
        {
            qe.WhereEqual(
                new ConditionExpression(fieldName, ConditionOperator.Like, $"%<entity name=\"{entityName}\">%name=\"{attributeName}\"%</entity>%"), 
                LogicalOperator.Or, 
                new ConditionExpression(fieldName, ConditionOperator.Like, $"%<entity name=\"{entityName}\">%attribute=\"{attributeName}\"%</entity>%"));

        }

        private void CopyData(IOrganizationService service, AttributeMetadata from, AttributeMetadata to, Action actions)
        {
            if (!MigrateData) { return; }

            var total = GetRecordCount(service, from);
            var count = 0;

            Trace("Copying data from {0} to {1}", from.LogicalName, to.LogicalName);
            var requests = new OrganizationRequestCollection();
            // Grab from and to, and only update if not equal.  This is to speed things up if it has failed part way through
            foreach (var entity in service.GetAllEntities<Entity>(new QueryExpression(from.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(from.LogicalName, to.LogicalName)
            }))
            {
                if (count++%100 == 0 || count == total)
                {
                    if (requests.Any())
                    {
                        PerformUpdates(service, requests);
                    }

                    Trace("Copying {0} / {1}", count, total);
                    requests.Clear();
                }

                var value = entity.GetAttributeValue<Object>(from.LogicalName);
                if (actions.HasFlag(Action.ChangeType) && from.GetType() != to.GetType())
                {
                    value = CopyValue(from, to, value);
                }
                var toValue = entity.GetAttributeValue<Object>(to.LogicalName);

                if (value != null)
                {
                    if (value.Equals(toValue)) { continue; }

                    entity.Attributes[to.LogicalName] = value;
                    requests.Add(new UpdateRequest
                    {
                        Target = entity
                    });
                }
                else if (toValue != null)
                {
                    entity.Attributes[to.LogicalName] = null;
                    requests.Add(new UpdateRequest
                    {
                        Target = entity
                    });
                }
            }

            if (requests.Any())
            {
                PerformUpdates(service, requests);
            }

            Trace("Data Migration Complete", count, total);
        }

        private void PerformUpdates(IOrganizationService service, OrganizationRequestCollection requests)
        {
            if (SupportsExecuteMultipleRequest)
            {
                var response = (ExecuteMultipleResponse) service.Execute(new ExecuteMultipleRequest
                {
                    Settings = new ExecuteMultipleSettings
                    {
                        ContinueOnError = false,
                        ReturnResponses = false
                    },
                    Requests = requests
                });

                if (response.IsFaulted)
                {
                    var fault = response.Responses.First().Fault;
                    while (fault.InnerFault != null)
                    {
                        fault = fault.InnerFault;
                    }

                    var errorDetails = string.Empty;
                    if (fault.ErrorDetails.ContainsKey("CallStack"))
                    {
                        errorDetails = Environment.NewLine + fault.ErrorDetails["CallStack"];
                    }

                    errorDetails += string.Format("{0}{0}TRACE TEXT:{0}{1}", Environment.NewLine, fault.TraceText);

                    throw new Exception(fault.Message + errorDetails);
                }
            }
            else
            {
                foreach (var request in requests)
                {
                    service.Update(((UpdateRequest) request).Target);
                }
            }
        }

        private int GetRecordCount(IOrganizationService service, AttributeMetadata from)
        {
            Trace("Retrieving {0} id attribute name", from.EntityLogicalName);
            var response = (RetrieveEntityResponse) service.Execute(new RetrieveEntityRequest
            {
                LogicalName = from.EntityLogicalName,
                EntityFilters = EntityFilters.Entity
            });

            Trace("Determining record count (accurate only up to 50000)");
            var xml = string.Format(@"
            <fetch distinct='false' mapping='logical' aggregate='true'> 
                <entity name='{0}'> 
                   <attribute name='{1}' alias='{1}_count' aggregate='count'/> 
                </entity> 
            </fetch>", from.EntityLogicalName, response.EntityMetadata.PrimaryIdAttribute);

            int total;
            try
            {
                var resultEntity = service.RetrieveMultiple(new FetchExpression(xml)).Entities.First();
                total = resultEntity.GetAliasedValue<int>(response.EntityMetadata.PrimaryIdAttribute + "_count");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("AggregateQueryRecordLimit exceeded"))
                {
                    total = 50000;
                }
                else
                {
                    throw;
                }
            }

            return total;
        }

        private AttributeMetadata CreateAttributeWithDifferentName(IOrganizationService service, AttributeMetadata existingAtt, string newSchemaName, AttributeMetadata newAttributeType)
        {
            Trace("Creating Attribute " + existingAtt.EntityLogicalName + "." + newSchemaName);
            AttributeMetadata clone;
            try
            {
                clone = CreateAttributeWithDifferentNameInternal(service, (dynamic) existingAtt, newSchemaName, newAttributeType);
            }
            catch
            {
                Trace("Error Creating Attribute " + existingAtt.EntityLogicalName + "." + newSchemaName);
                throw;
            }

            PublishEntity(service, existingAtt.EntityLogicalName);

            return clone;
        }

        private void RemoveInvalidLanguageLocalizedLabels(Label label)
        {
            if (label == null)
            {
                return;
            }

            var labelsToRemove = label.LocalizedLabels.Where(local => !ValidLanguageCodes.Contains(local.LanguageCode)).ToList();

            if (label.UserLocalizedLabel != null && !ValidLanguageCodes.Contains(label.UserLocalizedLabel.LanguageCode))
            {
                Trace("UserLocalizedLabel was invalid.  Removing Localization Label '{0}' for language code '{1}'", label.UserLocalizedLabel.Label, label.UserLocalizedLabel.LanguageCode);
                label.UserLocalizedLabel = null;
            }

            foreach (var local in labelsToRemove)
            {
                Trace("Removing Localization Label '{0}' for language code '{1}'", local.Label, local.LanguageCode);
                label.LocalizedLabels.Remove(local);
            }

            labelsToRemove.Clear();
        }

        private void SetEntityLogicalName(AttributeMetadata att, string entityLogicalName)
        {
            var prop = att.GetType().GetProperty("EntityLogicalName");
            prop.SetValue(att, entityLogicalName);
        }

        private void PublishEntity(IOrganizationService service, string logicalName)
        {
            Trace("Publishing Entity " + logicalName);
            service.Execute(new PublishXmlRequest
            {
                ParameterXml = "<importexportxml>" + "    <entities>" + "        <entity>" + logicalName + "</entity>" + "    </entities>" + "</importexportxml>"
            });
        }

        private void Trace(string message)
        {
            Debug.Assert(OnLog != null, "OnLog != null");
            OnLog(message);
        }

        private void Trace(string messageFormat, params object[] args)
        {
            Debug.Assert(OnLog != null, "OnLog != null");
            OnLog(string.Format(messageFormat, args));
        }

        private class AttributeMigrationState
        {
            public AttributeMetadata Old { get; set; }
            public AttributeMetadata Temp { get; set; }
            public AttributeMetadata New { get; set; }
        }
    }
}
