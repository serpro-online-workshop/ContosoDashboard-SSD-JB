using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ContosoDashboard.Models;

public class Document
{
    [Key]
    public int DocumentId { get; set; }

    [Required]
    [MaxLength(255)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [Required]
    [MaxLength(50)]
    public string Category { get; set; } = DocumentCategories.Other;

    // Normalized lowercase, comma-separated; max 10 tags, each at most 30 characters
    [MaxLength(500)]
    public string? Tags { get; set; }

    [Required]
    [MaxLength(255)]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string FileType { get; set; } = string.Empty;

    // Bytes; valid range 1 to 26,214,400
    public long FileSize { get; set; }

    // Relative storage key: {userId}/{projectId or "personal"}/{guid}.{ext}
    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    [Required]
    public int UploaderUserId { get; set; }

    public int? ProjectId { get; set; }

    public int? TaskId { get; set; }

    public DateTime UploadDate { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedDate { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(20)]
    public string ScanStatus { get; set; } = ScanStatuses.Pending;

    public Guid ScanAttemptId { get; set; }

    public DateTime ScanRequestedDate { get; set; } = DateTime.UtcNow;

    public DateTime? ScanCompletedDate { get; set; }

    [MaxLength(500)]
    public string? ScanDetail { get; set; }

    // Navigation properties
    [ForeignKey("UploaderUserId")]
    public virtual User UploaderUser { get; set; } = null!;

    [ForeignKey("ProjectId")]
    public virtual Project? Project { get; set; }

    [ForeignKey("TaskId")]
    public virtual TaskItem? Task { get; set; }

    public virtual ICollection<DocumentShare> Shares { get; set; } = new List<DocumentShare>();
}
