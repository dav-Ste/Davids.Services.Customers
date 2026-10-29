namespace Davids.Services.Customers.Core.Entities;

public enum CustomerState
{
    Unknown = 0,
    Valid = 1,
    Incomplete = 2,
    Suspicious = 3,
    Locked = 4
}
