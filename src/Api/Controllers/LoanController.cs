using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CashLoanManagement.Application.Common.Interfaces;
using CashLoanManagement.Application.DTOs.Loan;
using CashLoanManagement.Domain.Entities;
using CashLoanManagement.Domain.Enums;
using CashLoanManagement.Infrastructure.Jobs;
using CashLoanManagement.Infrastructure.Persistence;

namespace CashLoanManagement.Api.Controllers;

[Authorize]
public class LoanController : BaseApiController
{
    private readonly CashLoanDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly CashController _cashHelper;

    public LoanController(CashLoanDbContext context, INotificationService notificationService, CashController cashHelper)
    {
        _context = context;
        _notificationService = notificationService;
        _cashHelper = cashHelper;
    }

    // ════════════════════════════════════════════════════════════════════════
    // BORROWER ENDPOINTS
    // ════════════════════════════════════════════════════════════════════════

    [HttpGet("borrowers")]
    public async Task<IActionResult> GetBorrowers()
    {
        var borrowers = await _context.Borrowers
            .Include(b => b.Loans)
            .OrderBy(b => b.FullName)
            .Select(b => new
            {
                b.Id, b.FullName, b.NationalId, b.Phone, b.Address,
                b.EmploymentStatus, b.GuarantorDetails, b.IsBlacklisted,
                b.BlacklistedReason, b.BlacklistedAt,
                activeLoans = b.Loans.Count(l => l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue),
                totalLoans  = b.Loans.Count
            })
            .ToListAsync();
        return Ok(borrowers);
    }

    [HttpGet("borrowers/{id:int}")]
    public async Task<IActionResult> GetBorrower(int id)
    {
        var borrower = await _context.Borrowers
            .Include(b => b.Loans).ThenInclude(l => l.Repayments)
            .Include(b => b.Loans).ThenInclude(l => l.CreatedByUser)
            .Include(b => b.BlacklistedByUser)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (borrower == null) return NotFound(new { message = "Borrower not found." });

        return Ok(new
        {
            borrower.Id, borrower.FullName, borrower.NationalId, borrower.Phone,
            borrower.Address, borrower.EmploymentStatus, borrower.GuarantorDetails,
            borrower.PhotoUrl, borrower.IsBlacklisted, borrower.BlacklistedReason,
            borrower.BlacklistedAt, borrower.CreatedAt,
            blacklistedBy = borrower.BlacklistedByUser?.FullName,
            loans = borrower.Loans.OrderByDescending(l => l.CreatedAt).Select(l => new
            {
                l.Id, l.ReferenceNumber, l.Amount, l.Status, l.DueDate,
                l.CreatedAt, l.ApprovedAt, l.DisbursedAt,
                totalRepaid      = l.Repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Approved).Sum(r => r.Amount),
                remainingBalance = l.Amount - l.Repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Approved).Sum(r => r.Amount),
                createdBy        = l.CreatedByUser.FullName
            })
        });
    }

    [HttpPost("borrower")]
    [Authorize(Roles = "Admin,Cashier")]
    public async Task<IActionResult> CreateBorrower([FromBody] CreateBorrowerDto request)
    {
        if (await _context.Borrowers.AnyAsync(b => b.NationalId == request.NationalId))
            return BadRequest(new { message = $"A borrower with National ID '{request.NationalId}' already exists." });

        var borrower = new Borrower
        {
            FullName = request.FullName, NationalId = request.NationalId,
            Phone = request.Phone, Address = request.Address,
            EmploymentStatus = request.EmploymentStatus,
            GuarantorDetails = request.GuarantorDetails,
            CreatedAt = DateTime.UtcNow
        };
        _context.Borrowers.Add(borrower);
        await LogAuditAsync(GetCurrentUserId(), $"Borrower registered: {request.FullName} (NID: {request.NationalId})");
        await _context.SaveChangesAsync();

        return Ok(new { message = "Borrower registered.", borrower.Id, borrower.FullName, borrower.NationalId });
    }

    [HttpPut("borrowers/{id:int}")]
    [Authorize(Roles = "Admin,Cashier")]
    public async Task<IActionResult> UpdateBorrower(int id, [FromBody] UpdateBorrowerDto request)
    {
        var borrower = await _context.Borrowers.FindAsync(id);
        if (borrower == null) return NotFound(new { message = "Borrower not found." });

        if (!string.IsNullOrWhiteSpace(request.Phone))            borrower.Phone            = request.Phone;
        if (!string.IsNullOrWhiteSpace(request.Address))          borrower.Address          = request.Address;
        if (!string.IsNullOrWhiteSpace(request.EmploymentStatus)) borrower.EmploymentStatus = request.EmploymentStatus;
        if (request.GuarantorDetails != null)                      borrower.GuarantorDetails = request.GuarantorDetails;
        borrower.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync(GetCurrentUserId(), $"Borrower updated: {borrower.FullName} (ID: {id})");
        await _context.SaveChangesAsync();
        return Ok(new { message = "Borrower updated successfully." });
    }

    [HttpPost("borrowers/{id:int}/remove-blacklist")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RemoveFromBlacklist(int id, [FromBody] string reason)
    {
        var borrower = await _context.Borrowers.FindAsync(id);
        if (borrower == null) return NotFound(new { message = "Borrower not found." });
        if (!borrower.IsBlacklisted) return BadRequest(new { message = "Borrower is not blacklisted." });

        borrower.IsBlacklisted      = false;
        borrower.BlacklistedReason  = null;
        borrower.BlacklistedAt      = null;
        borrower.BlacklistedByUserId = null;
        borrower.UpdatedAt          = DateTime.UtcNow;

        // Also update any blacklisted loans back to overdue (they remain overdue but not blacklisted)
        var blacklistedLoans = await _context.Loans
            .Where(l => l.BorrowerId == id && l.Status == LoanStatus.Blacklisted)
            .ToListAsync();
        foreach (var loan in blacklistedLoans) { loan.Status = LoanStatus.Overdue; loan.UpdatedAt = DateTime.UtcNow; }

        await LogAuditAsync(GetCurrentUserId(), $"Borrower {borrower.FullName} removed from blacklist. Reason: {reason}");
        await _context.SaveChangesAsync();

        return Ok(new { message = $"{borrower.FullName} removed from blacklist." });
    }

    // ════════════════════════════════════════════════════════════════════════
    // LOAN ENDPOINTS
    // ════════════════════════════════════════════════════════════════════════

    [HttpGet("list")]
    public async Task<IActionResult> GetLoans([FromQuery] string? status)
    {
        var query = _context.Loans
            .Include(l => l.Borrower)
            .Include(l => l.Repayments)
            .Include(l => l.CreatedByUser)
            .Include(l => l.ApprovedByUser)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<LoanStatus>(status, out var s))
            query = query.Where(l => l.Status == s);

        var loans = await query
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new
            {
                l.Id, l.ReferenceNumber, l.Amount, l.RepaymentTerms,
                l.DueDate, l.Status, l.CreatedAt, l.ApprovedAt, l.DisbursedAt,
                TotalRepaid      = l.Repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Approved).Sum(r => r.Amount),
                RemainingBalance = l.Amount - l.Repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Approved).Sum(r => r.Amount),
                Borrower   = new { l.Borrower.Id, l.Borrower.FullName, l.Borrower.NationalId, l.Borrower.Phone },
                CreatedBy  = l.CreatedByUser.FullName,
                ApprovedBy = l.ApprovedByUser != null ? l.ApprovedByUser.FullName : null
            })
            .ToListAsync();

        return Ok(loans);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetLoan(int id)
    {
        var loan = await _context.Loans
            .Include(l => l.Borrower)
            .Include(l => l.CreatedByUser)
            .Include(l => l.ApprovedByUser)
            .Include(l => l.Repayments).ThenInclude(r => r.CapturedByUser)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loan == null) return NotFound(new { message = "Loan not found." });

        var totalRepaid = loan.Repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Approved).Sum(r => r.Amount);
        var remaining   = loan.Amount - totalRepaid;
        var daysOverdue = loan.Status == LoanStatus.Overdue
            ? (int)(DateTime.UtcNow.Date - loan.DueDate.Date).TotalDays : 0;

        return Ok(new
        {
            loan.Id, loan.ReferenceNumber, loan.Amount, loan.RepaymentTerms,
            loan.DueDate, loan.Status, loan.CreatedAt, loan.ApprovedAt, loan.DisbursedAt,
            totalRepaid, remainingBalance = remaining, daysOverdue,
            repaymentProgress = loan.Amount > 0 ? Math.Round((totalRepaid / loan.Amount) * 100, 1) : 0,
            borrower   = new { loan.Borrower.Id, loan.Borrower.FullName, loan.Borrower.NationalId, loan.Borrower.Phone },
            createdBy  = loan.CreatedByUser.FullName,
            approvedBy = loan.ApprovedByUser?.FullName,
            repayments = loan.Repayments
                .OrderByDescending(r => r.RepaymentDate)
                .Select(r => new
                {
                    r.Id, r.Amount, r.RepaymentDate, r.Reference,
                    status = r.ApprovalStatus.ToString(),
                    capturedBy = r.CapturedByUser.FullName
                })
        });
    }

    // ─── DAY LOAN: borrow in the morning, repay in the evening ────────────────
    /// <summary>
    /// Creates a same-day loan that is immediately active and disbursed (one step),
    /// due by end of today. Repaid in the evening via the normal repayment flow.
    /// </summary>
    [HttpPost("day-loan")]
    [Authorize(Roles = "Admin,Cashier")]
    public async Task<IActionResult> CreateDayLoan([FromBody] CreateDayLoanDto request)
    {
        var borrower = await _context.Borrowers.FindAsync(request.BorrowerId);
        if (borrower == null) return NotFound(new { message = "Borrower not found." });
        if (borrower.IsBlacklisted)
            return BadRequest(new { message = $"'{borrower.FullName}' is blacklisted and cannot receive a loan." });
        if (request.Amount <= 0)
            return BadRequest(new { message = "Amount must be greater than zero." });

        var hasOpenLoan = await _context.Loans.AnyAsync(l =>
            l.BorrowerId == request.BorrowerId &&
            (l.Status == LoanStatus.Active || l.Status == LoanStatus.Approved ||
             l.Status == LoanStatus.Pending || l.Status == LoanStatus.Overdue));
        if (hasOpenLoan)
            return BadRequest(new { message = "Borrower already has an open loan." });

        var balance = await _cashHelper.ComputeBalanceAsync();
        if (request.Amount > balance)
            return BadRequest(new { message = $"Insufficient cash. Available: ${balance:N2}, Required: ${request.Amount:N2}." });

        var userId    = GetCurrentUserId();
        var reference = $"DL-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpper()}";
        // Due end of today (UTC)
        var dueToday  = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Utc);

        var loan = new Loan
        {
            ReferenceNumber = reference, BorrowerId = request.BorrowerId,
            Amount = request.Amount, Type = LoanType.DayLoan,
            RepaymentTerms = "Same-day — borrow in the morning, repay by evening.",
            DueDate = dueToday, Status = LoanStatus.Active,
            CreatedByUserId = userId, ApprovedByUserId = userId,
            ApprovedAt = DateTime.UtcNow, DisbursedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        _context.Loans.Add(loan);

        // Immediate cash disbursement (approved)
        _context.CashTransactions.Add(new CashTransaction
        {
            Amount = request.Amount, Type = TransactionType.Disbursement,
            SourceOrPurpose = $"Day loan — {borrower.FullName} ({reference})" + (request.Notes != null ? $" — {request.Notes}" : ""),
            Reference = reference, Date = DateTime.UtcNow,
            ApprovalStatus = CashApprovalStatus.Approved,
            ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow,
            CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });

        await LogAuditAsync(userId, $"Day loan issued: {reference} ${request.Amount:N2} to {borrower.FullName}. Due today.");
        await _context.SaveChangesAsync();

        return Ok(new
        {
            message = $"Day loan {reference} issued and disbursed. Due by end of today.",
            loanId = loan.Id, referenceNumber = reference,
            dueDate = dueToday, newCashBalance = await _cashHelper.ComputeBalanceAsync()
        });
    }

    [HttpPost("create")]
    [Authorize(Roles = "Admin,Cashier")]
    public async Task<IActionResult> CreateLoan([FromBody] CreateLoanDto request)
    {
        var borrower = await _context.Borrowers.FindAsync(request.BorrowerId);
        if (borrower == null) return NotFound(new { message = "Borrower not found." });
        if (borrower.IsBlacklisted)
            return BadRequest(new { message = $"'{borrower.FullName}' is blacklisted and cannot receive a loan." });

        var hasOpenLoan = await _context.Loans.AnyAsync(l =>
            l.BorrowerId == request.BorrowerId &&
            (l.Status == LoanStatus.Active || l.Status == LoanStatus.Approved || l.Status == LoanStatus.Pending));
        if (hasOpenLoan)
            return BadRequest(new { message = "Borrower already has an active or pending loan." });

        var userId    = GetCurrentUserId();
        var reference = $"LN-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpper()}";

        var loan = new Loan
        {
            ReferenceNumber = reference, BorrowerId = request.BorrowerId,
            Amount = request.Amount, RepaymentTerms = request.RepaymentTerms,
            DueDate = request.DueDate, Status = LoanStatus.Pending,
            CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        };
        _context.Loans.Add(loan);

        _context.ApprovalWorkflows.Add(new ApprovalWorkflow
        {
            TargetType = WorkflowTargetType.Loan, TargetId = 0,
            Status = ApprovalStatus.Pending, RequestedByUserId = userId,
            Comment = "Awaiting approval.", CreatedAt = DateTime.UtcNow
        });

        await LogAuditAsync(userId, $"Loan created: {reference} for {borrower.FullName}, amount ${request.Amount:N2}");
        await _context.SaveChangesAsync();

        var workflow = await _context.ApprovalWorkflows
            .OrderByDescending(w => w.Id)
            .FirstAsync(w => w.RequestedByUserId == userId && w.TargetType == WorkflowTargetType.Loan);
        workflow.TargetId = loan.Id;
        await _context.SaveChangesAsync();

        await _notificationService.NotifyRoleAsync("Finance Officer", "New Loan Request",
            $"Loan {reference} for {borrower.FullName} (${request.Amount:N2}) awaiting approval.", NotificationType.PendingApproval);
        await _notificationService.NotifyRoleAsync("Manager", "New Loan Request",
            $"Loan {reference} for {borrower.FullName} (${request.Amount:N2}) awaiting approval.", NotificationType.PendingApproval);

        return Ok(new { message = "Loan request created.", loanId = loan.Id, referenceNumber = reference, status = "Pending" });
    }

    // ─── APPROVE: Pending → Approved (MAKER-CHECKER step 1) ──────────────────
    [HttpPost("approve/{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ApproveLoan(int id, [FromBody] string comment)
    {
        var loan = await _context.Loans.Include(l => l.Borrower).FirstOrDefaultAsync(l => l.Id == id);
        if (loan == null) return NotFound(new { message = "Loan not found." });
        if (loan.Status != LoanStatus.Pending)
            return BadRequest(new { message = $"Loan must be Pending to approve. Current: {loan.Status}." });

        var userId = GetCurrentUserId();
        loan.Status           = LoanStatus.Approved;  // ← Now Approved, not yet Active
        loan.ApprovedByUserId = userId;
        loan.ApprovedAt       = DateTime.UtcNow;
        loan.UpdatedAt        = DateTime.UtcNow;

        var workflow = await _context.ApprovalWorkflows
            .FirstOrDefaultAsync(w => w.TargetType == WorkflowTargetType.Loan && w.TargetId == id && w.Status == ApprovalStatus.Pending);
        if (workflow != null)
        {
            workflow.Status = ApprovalStatus.Approved;
            workflow.Comment = comment ?? string.Empty;
            workflow.ApprovedOrRejectedByUserId = userId;
            workflow.UpdatedAt = DateTime.UtcNow;
        }

        await LogAuditAsync(userId, $"Loan {loan.ReferenceNumber} APPROVED. Comment: {comment}");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyUserAsync(loan.CreatedByUserId, "Loan Approved — Ready for Disbursement",
            $"Loan {loan.ReferenceNumber} for {loan.Borrower.FullName} approved. Proceed to disburse.", NotificationType.PendingApproval);

        return Ok(new { message = $"Loan {loan.ReferenceNumber} approved. Now awaiting disbursement.", status = "Approved" });
    }

    // ─── DISBURSE: Approved → Active (MAKER-CHECKER step 2) ──────────────────
    [HttpPost("disburse/{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> DisburseLoan(int id)
    {
        var loan = await _context.Loans.Include(l => l.Borrower).FirstOrDefaultAsync(l => l.Id == id);
        if (loan == null) return NotFound(new { message = "Loan not found." });
        if (loan.Status != LoanStatus.Approved)
            return BadRequest(new { message = $"Loan must be Approved before disbursement. Current: {loan.Status}." });

        var balance = await _cashHelper.ComputeBalanceAsync();
        if (loan.Amount > balance)
            return BadRequest(new { message = $"Insufficient cash balance. Available: ${balance:N2}, Required: ${loan.Amount:N2}." });

        var userId = GetCurrentUserId();

        // Create auto-approved cash disbursement (loan disbursement is already checker-approved via loan approval)
        var tx = new CashTransaction
        {
            Amount = loan.Amount, Type = TransactionType.Disbursement,
            SourceOrPurpose = $"Loan disbursement — {loan.Borrower.FullName} ({loan.ReferenceNumber})",
            Reference = loan.ReferenceNumber, Date = DateTime.UtcNow,
            ApprovalStatus = CashApprovalStatus.Approved,
            ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow,
            CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        };
        _context.CashTransactions.Add(tx);

        loan.Status      = LoanStatus.Active;
        loan.DisbursedAt = DateTime.UtcNow;
        loan.UpdatedAt   = DateTime.UtcNow;

        await LogAuditAsync(userId, $"Loan {loan.ReferenceNumber} DISBURSED. ${loan.Amount:N2} deducted from cash. Borrower: {loan.Borrower.FullName}");
        await _context.SaveChangesAsync();

        var newBalance = await _cashHelper.ComputeBalanceAsync();

        await _notificationService.NotifyUserAsync(loan.CreatedByUserId, "Loan Disbursed — Now Active",
            $"Loan {loan.ReferenceNumber} for {loan.Borrower.FullName} has been disbursed and is now Active.", NotificationType.PendingApproval);

        if (newBalance < 5000m)
            await _notificationService.NotifyRoleAsync("Manager", "Low Cash Balance After Loan Disbursement",
                $"Balance is ${newBalance:N2} after disbursing loan {loan.ReferenceNumber}.", NotificationType.LowCashBalance);

        return Ok(new { message = $"Loan {loan.ReferenceNumber} disbursed. Loan is now Active.", status = "Active", newCashBalance = newBalance });
    }

    // ─── REJECT: Pending → Defaulted ─────────────────────────────────────────
    [HttpPost("reject/{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> RejectLoan(int id, [FromBody] string comment)
    {
        var loan = await _context.Loans.Include(l => l.Borrower).FirstOrDefaultAsync(l => l.Id == id);
        if (loan == null) return NotFound(new { message = "Loan not found." });
        if (loan.Status != LoanStatus.Pending && loan.Status != LoanStatus.Approved)
            return BadRequest(new { message = $"Cannot reject loan with status '{loan.Status}'." });

        var userId = GetCurrentUserId();
        loan.Status    = LoanStatus.Defaulted;
        loan.UpdatedAt = DateTime.UtcNow;

        var workflow = await _context.ApprovalWorkflows
            .FirstOrDefaultAsync(w => w.TargetType == WorkflowTargetType.Loan && w.TargetId == id && w.Status == ApprovalStatus.Pending);
        if (workflow != null)
        {
            workflow.Status = ApprovalStatus.Rejected; workflow.Comment = comment ?? string.Empty;
            workflow.ApprovedOrRejectedByUserId = userId; workflow.UpdatedAt = DateTime.UtcNow;
        }

        await LogAuditAsync(userId, $"Loan {loan.ReferenceNumber} REJECTED. Reason: {comment}");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyUserAsync(loan.CreatedByUserId, "Loan Rejected",
            $"Loan {loan.ReferenceNumber} for {loan.Borrower.FullName} was rejected. Reason: {comment}", NotificationType.PendingApproval);

        return Ok(new { message = $"Loan {loan.ReferenceNumber} rejected." });
    }

    // ─── REPAYMENT (MAKER → pending) ──────────────────────────────────────────
    /// <summary>Repayments require checker approval — the cash receipt is only posted and
    /// the loan status only updates once a Manager/Admin approves.</summary>
    [HttpPost("repayment")]
    [Authorize(Roles = "Admin,Cashier")]
    public async Task<IActionResult> CaptureRepayment([FromBody] CaptureRepaymentDto request)
    {
        var loan = await _context.Loans
            .Include(l => l.Repayments)
            .Include(l => l.Borrower)
            .FirstOrDefaultAsync(l => l.Id == request.LoanId);

        if (loan == null) return NotFound(new { message = "Loan not found." });
        if (loan.Status != LoanStatus.Active && loan.Status != LoanStatus.Overdue)
            return BadRequest(new { message = $"Cannot record repayment for loan with status '{loan.Status}'." });
        if (request.Amount <= 0)
            return BadRequest(new { message = "Repayment amount must be greater than zero." });

        var approvedRepaid = loan.Repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Approved).Sum(r => r.Amount);
        var pendingRepaid   = loan.Repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Pending).Sum(r => r.Amount);
        var remaining       = loan.Amount - approvedRepaid - pendingRepaid;

        if (request.Amount > remaining)
            return BadRequest(new { message = $"Amount ${request.Amount:N2} exceeds remaining balance ${remaining:N2} (after pending repayments)." });

        var userId = GetCurrentUserId();

        // System-generated sequential reference: REP-yyyyMMdd-NNNN
        var todayUtc    = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var tomorrowUtc = todayUtc.AddDays(1);
        var repCountToday = await _context.LoanRepayments
            .CountAsync(r => r.RepaymentDate >= todayUtc && r.RepaymentDate < tomorrowUtc);
        var reference = $"REP-{todayUtc:yyyyMMdd}-{(repCountToday + 1):D4}";

        var repayment = new LoanRepayment
        {
            LoanId = loan.Id, Amount = request.Amount,
            Reference = reference, ApprovalStatus = CashApprovalStatus.Pending,
            RepaymentDate = DateTime.UtcNow, CapturedByUserId = userId, CreatedAt = DateTime.UtcNow
        };
        _context.LoanRepayments.Add(repayment);

        await LogAuditAsync(userId, $"Repayment ${request.Amount:N2} requested for loan {loan.ReferenceNumber} ({loan.Borrower.FullName}). AWAITING APPROVAL.");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyRoleAsync("Manager", "Loan Repayment Pending Approval",
            $"${request.Amount:N2} repayment for {loan.ReferenceNumber} ({loan.Borrower.FullName}) needs approval.", NotificationType.PendingApproval);

        return Ok(new
        {
            message = "Repayment submitted for approval. It will reflect once a checker approves it.",
            loanReference = loan.ReferenceNumber,
            reference,
            status = "PendingApproval"
        });
    }

    // ─── APPROVE REPAYMENT (CHECKER) ──────────────────────────────────────────
    [HttpGet("pending-repayments")]
    public async Task<IActionResult> GetPendingRepayments()
    {
        var pending = await _context.LoanRepayments
            .Include(r => r.Loan).ThenInclude(l => l.Borrower)
            .Include(r => r.CapturedByUser)
            .Where(r => r.ApprovalStatus == CashApprovalStatus.Pending)
            .OrderBy(r => r.RepaymentDate)
            .Select(r => new
            {
                r.Id, r.Amount, r.Reference, r.RepaymentDate,
                loanId = r.LoanId, loanReference = r.Loan.ReferenceNumber,
                borrowerName = r.Loan.Borrower.FullName,
                requestedBy = r.CapturedByUser.FullName
            })
            .ToListAsync();
        return Ok(pending);
    }

    [HttpPost("repayment/{id:int}/approve")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ApproveRepayment(int id)
    {
        var repayment = await _context.LoanRepayments
            .Include(r => r.Loan).ThenInclude(l => l.Borrower)
            .Include(r => r.Loan).ThenInclude(l => l.Repayments)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (repayment == null) return NotFound(new { message = "Repayment not found." });
        if (repayment.ApprovalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = $"Repayment is not pending. Status: {repayment.ApprovalStatus}." });

        var userId = GetCurrentUserId();
        var loan = repayment.Loan;

        repayment.ApprovalStatus = CashApprovalStatus.Approved;
        repayment.ApprovedByUserId = userId;
        repayment.ApprovedAt = DateTime.UtcNow;
        repayment.UpdatedAt = DateTime.UtcNow;

        _context.CashTransactions.Add(new CashTransaction
        {
            Amount = repayment.Amount, Type = TransactionType.Addition,
            SourceOrPurpose = $"Loan repayment — {loan.Borrower.FullName} ({loan.ReferenceNumber})",
            Reference = repayment.Reference, Date = DateTime.UtcNow,
            ApprovalStatus = CashApprovalStatus.Approved,
            ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow,
            CreatedByUserId = userId, CreatedAt = DateTime.UtcNow
        });

        var totalRepaid = loan.Repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Approved || r.Id == repayment.Id).Sum(r => r.Amount);
        var newRemaining = loan.Amount - totalRepaid;
        if (newRemaining <= 0)
        {
            loan.Status    = LoanStatus.Paid;
            loan.UpdatedAt = DateTime.UtcNow;
        }

        await LogAuditAsync(userId, $"Repayment ${repayment.Amount:N2} for loan {loan.ReferenceNumber} ({loan.Borrower.FullName}) APPROVED. Remaining: ${newRemaining:N2}");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyUserAsync(repayment.CapturedByUserId, "Loan Repayment Approved",
            $"The ${repayment.Amount:N2} repayment for {loan.ReferenceNumber} has been approved.", NotificationType.PendingApproval);

        if (loan.Status == LoanStatus.Paid)
            await _notificationService.NotifyRoleAsync("Manager", "Loan Fully Repaid",
                $"Loan {loan.ReferenceNumber} ({loan.Borrower.FullName}) has been fully repaid.", NotificationType.OverdueLoan);

        return Ok(new
        {
            message = "Repayment approved.",
            remainingBalance = newRemaining,
            loanStatus = loan.Status.ToString(),
            totalRepaid,
            repaymentProgress = loan.Amount > 0 ? Math.Round((totalRepaid / loan.Amount) * 100, 1) : 0
        });
    }

    [HttpPost("repayment/{id:int}/reject")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> RejectRepayment(int id, [FromBody] string reason)
    {
        var repayment = await _context.LoanRepayments
            .Include(r => r.Loan).ThenInclude(l => l.Borrower)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (repayment == null) return NotFound(new { message = "Repayment not found." });
        if (repayment.ApprovalStatus != CashApprovalStatus.Pending)
            return BadRequest(new { message = "Repayment is not pending." });

        var userId = GetCurrentUserId();
        repayment.ApprovalStatus = CashApprovalStatus.Rejected;
        repayment.RejectionReason = reason;
        repayment.ApprovedByUserId = userId;
        repayment.UpdatedAt = DateTime.UtcNow;

        await LogAuditAsync(userId, $"Repayment ${repayment.Amount:N2} for loan {repayment.Loan.ReferenceNumber} REJECTED. Reason: {reason}");
        await _context.SaveChangesAsync();

        await _notificationService.NotifyUserAsync(repayment.CapturedByUserId, "Loan Repayment Rejected",
            $"The ${repayment.Amount:N2} repayment for {repayment.Loan.ReferenceNumber} was rejected. Reason: {reason}", NotificationType.PendingApproval);

        return Ok(new { message = "Repayment rejected.", reason });
    }

    // ─── MANUAL OVERDUE / BLACKLIST CHECK ─────────────────────────────────────
    /// <summary>
    /// Runs the overdue-marking and auto-blacklist evaluations on demand
    /// (same logic as the scheduled daily jobs). Useful for UAT.
    /// </summary>
    [HttpPost("run-overdue-check")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> RunOverdueCheck([FromServices] LoanStatusJob job)
    {
        var overdueBefore     = await _context.Loans.CountAsync(l => l.Status == LoanStatus.Overdue);
        var blacklistedBefore = await _context.Borrowers.CountAsync(b => b.IsBlacklisted);

        await job.MarkOverdueLoansAsync();
        await job.AutoBlacklistDefaultersAsync();

        var overdueAfter     = await _context.Loans.CountAsync(l => l.Status == LoanStatus.Overdue);
        var blacklistedAfter = await _context.Borrowers.CountAsync(b => b.IsBlacklisted);

        await LogAuditAsync(GetCurrentUserId(),
            $"Manual overdue/blacklist check run. Overdue {overdueBefore}->{overdueAfter}, blacklisted {blacklistedBefore}->{blacklistedAfter}.");

        return Ok(new
        {
            message               = "Overdue check completed.",
            newlyOverdue          = overdueAfter  - overdueBefore,
            newlyBlacklisted      = blacklistedAfter - blacklistedBefore,
            totalOverdue          = overdueAfter,
            totalBlacklisted      = blacklistedAfter
        });
    }

    // ─── OVERDUE LIST ─────────────────────────────────────────────────────────
    [HttpGet("overdue")]
    public async Task<IActionResult> GetOverdueLoans()
    {
        var today = DateTime.UtcNow.Date;
        var raw   = await _context.Loans
            .Include(l => l.Borrower)
            .Where(l => l.Status == LoanStatus.Overdue)
            .OrderBy(l => l.DueDate)
            .Select(l => new { l.Id, l.ReferenceNumber, l.Borrower.FullName, l.Borrower.Phone, l.Borrower.NationalId, l.Amount, l.DueDate })
            .ToListAsync();

        var result = raw.Select(l =>
        {
            var days = (int)(today - l.DueDate.Date).TotalDays;
            return new
            {
                loanId = l.Id, l.ReferenceNumber, borrowerName = l.FullName,
                borrowerPhone = l.Phone, nationalId = l.NationalId,
                l.Amount, l.DueDate, daysOverdue = days,
                riskLevel = days >= 60 ? "Critical — Blacklist Pending" : days >= 31 ? "High Risk" : "Warning"
            };
        });

        return Ok(result);
    }

    // ─── BLACKLISTED ──────────────────────────────────────────────────────────
    [HttpGet("blacklisted")]
    public async Task<IActionResult> GetBlacklistedBorrowers()
    {
        var list = await _context.Borrowers
            .Include(b => b.BlacklistedByUser)
            .Where(b => b.IsBlacklisted)
            .Select(b => new
            {
                borrowerId = b.Id, b.FullName, b.NationalId, b.Phone,
                blacklistedReason = b.BlacklistedReason,
                blacklistedAt     = b.BlacklistedAt,
                blacklistedBy     = b.BlacklistedByUser != null ? b.BlacklistedByUser.FullName : "System (Auto)"
            })
            .ToListAsync();
        return Ok(list);
    }

    // ─── PENDING APPROVALS SUMMARY ────────────────────────────────────────────
    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingLoans()
    {
        var pendingApproval = await _context.Loans
            .Include(l => l.Borrower).Include(l => l.CreatedByUser)
            .Where(l => l.Status == LoanStatus.Pending)
            .OrderBy(l => l.CreatedAt)
            .Select(l => new
            {
                l.Id, l.ReferenceNumber, l.Amount, l.RepaymentTerms, l.DueDate,
                borrowerName = l.Borrower.FullName, borrowerNationalId = l.Borrower.NationalId,
                createdBy = l.CreatedByUser.FullName, l.CreatedAt,
                stage = "Awaiting Approval"
            })
            .ToListAsync();

        var pendingDisbursement = await _context.Loans
            .Include(l => l.Borrower).Include(l => l.ApprovedByUser)
            .Where(l => l.Status == LoanStatus.Approved)
            .OrderBy(l => l.ApprovedAt)
            .Select(l => new
            {
                l.Id, l.ReferenceNumber, l.Amount, l.RepaymentTerms, l.DueDate,
                borrowerName = l.Borrower.FullName, borrowerNationalId = l.Borrower.NationalId,
                approvedBy = l.ApprovedByUser != null ? l.ApprovedByUser.FullName : null,
                l.ApprovedAt,
                stage = "Awaiting Disbursement"
            })
            .ToListAsync();

        return Ok(new { pendingApproval, pendingDisbursement });
    }

    // ─── REPAYMENT STATEMENT ──────────────────────────────────────────────────
    [HttpGet("{id:int}/statement")]
    public async Task<IActionResult> GetLoanStatement(int id)
    {
        var loan = await _context.Loans
            .Include(l => l.Borrower)
            .Include(l => l.Repayments).ThenInclude(r => r.CapturedByUser)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (loan == null) return NotFound(new { message = "Loan not found." });

        var repayments = loan.Repayments.OrderBy(r => r.RepaymentDate).ToList();
        var approved   = repayments.Where(r => r.ApprovalStatus == CashApprovalStatus.Approved).ToList();
        decimal runningBalance = loan.Amount;
        var schedule = repayments.Select(r =>
        {
            var counts = r.ApprovalStatus == CashApprovalStatus.Approved;
            if (counts) runningBalance -= r.Amount;
            return new
            {
                r.Id, r.RepaymentDate, r.Amount, r.Reference,
                status = r.ApprovalStatus.ToString(),
                balanceAfter = counts ? (decimal?)runningBalance : null,
                capturedBy   = r.CapturedByUser.FullName
            };
        }).ToList();

        return Ok(new
        {
            loan.Id, loan.ReferenceNumber, loan.Amount, loan.RepaymentTerms,
            loan.DueDate, loan.Status, loan.DisbursedAt,
            borrower         = new { loan.Borrower.FullName, loan.Borrower.NationalId, loan.Borrower.Phone },
            totalRepaid      = approved.Sum(r => r.Amount),
            remainingBalance = loan.Amount - approved.Sum(r => r.Amount),
            repaymentCount   = repayments.Count,
            schedule
        });
    }

    private async Task LogAuditAsync(int userId, string action)
    {
        _context.AuditLogs.Add(new AuditLog
        {
            Action = action, IpAddress = GetClientIp(), DeviceInfo = GetDeviceInfo(),
            Timestamp = DateTime.UtcNow, UserId = userId, CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
    }
}
