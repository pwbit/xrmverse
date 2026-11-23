namespace Xrmd.Model.Metadata;

// Member names and numeric values are the Web API ones; the CLR names are our own.

[WireType("AssociatedMenuBehavior")]
public enum MenuBehavior { UseCollectionName = 0, UseLabel = 1, DoNotDisplay = 2 }

[WireType("AssociatedMenuGroup")]
public enum MenuGroup { Details = 0, Sales = 1, Service = 2, Marketing = 3 }

[WireType("AttributeRequiredLevel")]
public enum RequirementLevel { None = 0, SystemRequired = 1, ApplicationRequired = 2, Recommended = 3 }

[WireType("AttributeTypeCode")]
public enum ColumnTypeCode
{
    Boolean = 0, Customer = 1, DateTime = 2, Decimal = 3, Double = 4, Integer = 5, Lookup = 6, Memo = 7, Money = 8, Owner = 9,
    PartyList = 10, Picklist = 11, State = 12, Status = 13, String = 14, Uniqueidentifier = 15, CalendarRules = 16, Virtual = 17,
    BigInt = 18, ManagedProperty = 19, EntityName = 20,
}

[WireType("CascadeType")]
public enum CascadeAction { NoCascade = 0, Cascade = 1, Active = 2, UserOwned = 3, RemoveLink = 4, Restrict = 5 }

[WireType("DateTimeFormat")]
public enum DateTimeDisplayFormat { DateOnly = 0, DateAndTime = 1 }

[WireType("EntityClusterMode")]
public enum TableClusterMode { Partitioned = 0, Replicated = 1, Local = 2, Inherited = 3 }

[WireType("EntityKeyIndexStatus")]
public enum KeyIndexState { Pending = 0, InProgress = 1, Active = 2, Failed = 3 }

[WireType("ImeMode")]
public enum InputMethodMode { Auto = 0, Inactive = 1, Active = 2, Disabled = 3 }

[WireType("IntegerFormat")]
public enum IntegerDisplayFormat { None = 0, Duration = 1, TimeZone = 2, Language = 3, Locale = 4 }

[WireType("LookupFormat")]
public enum LookupDisplayFormat { None = 0, Connection = 1, Regarding = 2, Text = 3 }

[WireType("OptionSetType")]
public enum ChoiceSetKind { Picklist = 0, State = 1, Status = 2, Boolean = 3 }

[Flags]
[WireType("OwnershipTypes")]
public enum TableOwnership { None = 0, UserOwned = 1, TeamOwned = 2, BusinessOwned = 4, OrganizationOwned = 8, BusinessParented = 16, Filtered = 32 }

[WireType("PrivilegeType")]
public enum PrivilegeKind { None = 0, Create = 1, Read = 2, Write = 3, Delete = 4, Assign = 5, Share = 6, Append = 7, AppendTo = 8 }

[WireType("RelationshipType")]
public enum RelationshipKind { OneToManyRelationship = 0, Default = 0, ManyToManyRelationship = 1 }

[Flags]
[WireType("SecurityTypes")]
public enum SecurityBehaviors { None = 0, Append = 1, ParentChild = 2, Pointer = 4, Inheritance = 8 }

[WireType("StringFormat")]
public enum TextDisplayFormat { Email = 0, Text = 1, TextArea = 2, Url = 3, TickerSymbol = 4, PhoneticGuide = 5, VersionNumber = 6, Phone = 7, Json = 8, RichText = 9 }
