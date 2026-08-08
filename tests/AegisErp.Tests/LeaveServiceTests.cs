using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

public class LeaveServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EmployeeService _employees;
    private readonly LeaveService _leave;
    private static readonly DateTime Now = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    public LeaveServiceTests()
    {
        _employees = new EmployeeService(_db);
        _leave = new LeaveService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task<Employee> Hire(DateOnly joiningDate) =>
        _employees.CreateAsync(
            new EmployeeInput("Ahmed", null, null, joiningDate, 5000, 0, 0, 0, null, null, null, null, _db.Expense.Id, null),
            "tester", Now);

    [Fact]
    public void AccruedDays_prorates_from_day_one_and_caps_at_30()
    {
        var joined = new DateOnly(2026, 1, 1);
        Assert.Equal(0, LeaveService.AccruedDays(joined, joined));
        Assert.Equal(15, LeaveService.AccruedDays(joined, joined.AddDays(183))); // ~half a year
        Assert.Equal(30, LeaveService.AccruedDays(joined, joined.AddDays(365)));
        Assert.Equal(30, LeaveService.AccruedDays(joined, joined.AddDays(365 * 3))); // capped
    }

    [Fact]
    public async Task GetBalanceAsync_subtracts_only_approved_annual_days()
    {
        var employee = await Hire(new DateOnly(2025, 5, 20)); // exactly 1 year before Now -> 30 accrued days

        var pending = await _leave.CreateRequestAsync(employee.Id, LeaveType.Annual, new(2026, 6, 1), new(2026, 6, 5), null, "tester", Now);
        var sick = await _leave.CreateRequestAsync(employee.Id, LeaveType.Sick, new(2026, 6, 10), new(2026, 6, 12), "flu", "tester", Now);
        await _leave.DecideAsync(sick.Id, approved: true, "manager", Now);

        var balanceBeforeApproval = await _leave.GetBalanceAsync(employee.Id, new(2026, 6, 1));
        Assert.Equal(30, balanceBeforeApproval.AccruedDays);
        Assert.Equal(0, balanceBeforeApproval.UsedDays); // sick leave doesn't touch the annual balance; annual request still pending
        Assert.Equal(30, balanceBeforeApproval.RemainingDays);

        await _leave.DecideAsync(pending.Id, approved: true, "manager", Now);
        var balanceAfterApproval = await _leave.GetBalanceAsync(employee.Id, new(2026, 6, 1));
        Assert.Equal(5, balanceAfterApproval.UsedDays);
        Assert.Equal(25, balanceAfterApproval.RemainingDays);
    }

    [Fact]
    public async Task CreateRequestAsync_rejects_annual_leave_exceeding_the_balance()
    {
        var employee = await Hire(new DateOnly(2026, 4, 1)); // ~7 weeks before Now -> very small accrual

        await Assert.ThrowsAsync<PostingException>(() =>
            _leave.CreateRequestAsync(employee.Id, LeaveType.Annual, new(2026, 6, 1), new(2026, 6, 30), null, "tester", Now));
    }

    [Fact]
    public async Task CreateRequestAsync_allows_unpaid_leave_regardless_of_annual_balance()
    {
        var employee = await Hire(new DateOnly(2026, 5, 1)); // no annual balance accrued yet

        var request = await _leave.CreateRequestAsync(employee.Id, LeaveType.Unpaid, new(2026, 6, 1), new(2026, 6, 10), null, "tester", Now);

        Assert.Equal(10, request.Days);
        Assert.Equal(LeaveRequestStatus.Pending, request.Status);
    }

    [Fact]
    public async Task DecideAsync_cannot_re_decide_an_already_decided_request()
    {
        var employee = await Hire(new DateOnly(2025, 1, 1));
        var request = await _leave.CreateRequestAsync(employee.Id, LeaveType.Sick, new(2026, 6, 1), new(2026, 6, 2), null, "tester", Now);

        await _leave.DecideAsync(request.Id, approved: false, "manager", Now);

        await Assert.ThrowsAsync<PostingException>(() => _leave.DecideAsync(request.Id, approved: true, "manager", Now));
    }

    [Fact]
    public async Task CreateRequestAsync_rejects_an_end_date_before_the_start_date()
    {
        var employee = await Hire(new DateOnly(2025, 1, 1));

        await Assert.ThrowsAsync<PostingException>(() =>
            _leave.CreateRequestAsync(employee.Id, LeaveType.Sick, new(2026, 6, 10), new(2026, 6, 1), null, "tester", Now));
    }

    [Fact]
    public async Task Leave_requests_of_another_company_are_not_visible()
    {
        var employee = await Hire(new DateOnly(2025, 1, 1));
        await _leave.CreateRequestAsync(employee.Id, LeaveType.Sick, new(2026, 6, 1), new(2026, 6, 2), null, "tester", Now);

        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var otherLeave = new LeaveService(_db);
        Assert.Empty(await otherLeave.GetAllAsync());
    }
}
