using System.Net.Mail;
using Davids.Services.Customers.Core.Exceptions;

namespace Davids.Services.Customers.Core.Entities;

public sealed class Customer
{
    private Customer() { }

    private Customer(Guid id, string email, DateTime createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new InvalidCustomerIdException();
        }

        Id = id;
        Email = email ?? string.Empty;
        CreatedAt = createdAt;
        State = CustomerState.Incomplete;
        CompletedOrders = new List<CustomerCompletedOrder>();
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string FullName { get; private set; } = string.Empty;
    public string Address { get; private set; } = string.Empty;
    public bool IsVip { get; private set; }
    public CustomerState State { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public List<CustomerCompletedOrder> CompletedOrders { get; private set; } = [];

    public static Customer CreateIncomplete(Guid id, string email, DateTime createdAt)
        => new(id, email, createdAt);

    public void CompleteRegistration(string fullName, string address)
    {
        ValidateFullName(fullName);
        ValidateAddress(address);

        if (State != CustomerState.Incomplete)
        {
            throw new CannotChangeCustomerStateException(Id, State);
        }

        FullName = fullName.Trim();
        Address = address.Trim();
        State = CustomerState.Valid;
    }

    public bool UpdateDetails(string fullName, string address, string? email = null)
    {
        ValidateFullName(fullName);
        ValidateAddress(address);

        var normalizedFullName = fullName.Trim();
        var normalizedAddress = address.Trim();
        var normalizedEmail = Email;

        if (!string.IsNullOrWhiteSpace(email))
        {
            ValidateEmail(email);
            normalizedEmail = email.Trim();
        }

        var changed = !string.Equals(FullName, normalizedFullName, StringComparison.Ordinal) ||
                      !string.Equals(Address, normalizedAddress, StringComparison.Ordinal) ||
                      !string.Equals(Email, normalizedEmail, StringComparison.Ordinal);

        FullName = normalizedFullName;
        Address = normalizedAddress;
        Email = normalizedEmail;
        return changed;
    }

    public CustomerState ChangeState(CustomerState state)
    {
        if (state == CustomerState.Unknown || !Enum.IsDefined(state))
        {
            throw new CannotChangeCustomerStateException(Id, state);
        }

        var previousState = State;
        State = state;
        return previousState;
    }

    public bool AddCompletedOrder(Guid orderId)
    {
        if (orderId == Guid.Empty || CompletedOrders.Any(order => order.OrderId == orderId))
        {
            return false;
        }

        CompletedOrders.Add(new CustomerCompletedOrder(Id, orderId));
        if (IsVip || CompletedOrders.Count < 20)
        {
            return false;
        }

        IsVip = true;
        return true;
    }

    private void ValidateFullName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 200 || fullName.Trim().Length > 200 || fullName.Contains('\0'))
        {
            throw new InvalidCustomerFullNameException(Id);
        }
    }

    private void ValidateAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 500 || address.Trim().Length > 500 || address.Contains('\0'))
        {
            throw new InvalidCustomerAddressException(Id);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex StrictEmailRegex = new(
        @"^[A-Za-z0-9_%+\-]{1,20}(?:\.[A-Za-z0-9_%+\-]{1,20}){0,2}@(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,30}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private void ValidateEmail(string? email)
    {
        var candidate = email?.Trim();
        var atIndex = candidate?.IndexOf('@') ?? -1;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(candidate) || email.Length < 6 || email.Length > 254 ||
            candidate.Length < 6 || candidate.Length > 254 || atIndex < 1 || atIndex > 64 || candidate.Contains('\0') ||
            !MailAddress.TryCreate(candidate, out var parsed) ||
            !string.Equals(parsed.Address, candidate, StringComparison.Ordinal) ||
            !StrictEmailRegex.IsMatch(candidate))
        {
            throw new InvalidCustomerEmailException(Id);
        }
    }
}
