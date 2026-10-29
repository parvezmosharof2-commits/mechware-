using ShopVerse.Domain.Common;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Reviews;

public enum ReviewStatus { Pending = 1, Published = 2, Rejected = 3 }

public sealed class Review : Entity<Guid>
{
    private readonly List<ReviewMedia> _media = [];

    public Guid ProductId { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>Verified-purchase link. Null until the order module exists (Phase 8/9) or for imported/seeded reviews.</summary>
    public Guid? OrderItemId { get; private set; }

    public int Rating { get; private set; }
    public string? Title { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public ReviewStatus Status { get; private set; } = ReviewStatus.Pending;
    public int HelpfulCount { get; private set; }
    public string? SellerReply { get; private set; }
    public DateTime? SellerRepliedAtUtc { get; private set; }
    public IReadOnlyCollection<ReviewMedia> Media => _media.AsReadOnly();

    private Review() { }

    public Review(Guid id, Guid productId, Guid userId, Guid? orderItemId, int rating, string? title, string body,
        ReviewStatus status, int helpfulCount, DateTime createdAtUtc) : base(id)
    {
        if (rating is < 1 or > 5) throw new ValidationException("rating", "Rating must be between 1 and 5.");
        if (string.IsNullOrWhiteSpace(body)) throw new ValidationException("body", "Review text is required.");
        ProductId = productId;
        UserId = userId;
        OrderItemId = orderItemId;
        Rating = rating;
        Title = title;
        Body = body;
        Status = status;
        HelpfulCount = helpfulCount;
        CreatedAtUtc = createdAtUtc;
    }

    public void AddPhoto(Guid id, string url) => _media.Add(new ReviewMedia(id, Id, url));

    public void ReplyAsSeller(string reply, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reply)) throw new ValidationException("reply", "Reply text is required.");
        SellerReply = reply.Trim();
        SellerRepliedAtUtc = nowUtc;
    }
}

public sealed class ReviewMedia : Entity<Guid>
{
    public Guid ReviewId { get; private set; }
    public string MediaUrl { get; private set; } = string.Empty;

    private ReviewMedia() { }

    internal ReviewMedia(Guid id, Guid reviewId, string mediaUrl) : base(id)
    {
        ReviewId = reviewId;
        MediaUrl = SafeUrl.Require(mediaUrl, "mediaUrl");
    }
}

public sealed class Question : Entity<Guid>
{
    private readonly List<Answer> _answers = [];

    public Guid ProductId { get; private set; }
    public Guid UserId { get; private set; }
    public string QuestionText { get; private set; } = string.Empty;
    public bool IsApproved { get; private set; }
    public IReadOnlyCollection<Answer> Answers => _answers.AsReadOnly();

    private Question() { }

    public Question(Guid id, Guid productId, Guid userId, string text, bool isApproved, DateTime createdAtUtc) : base(id)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ValidationException("questionText", "Question text is required.");
        ProductId = productId;
        UserId = userId;
        QuestionText = text.Trim();
        IsApproved = isApproved;
        CreatedAtUtc = createdAtUtc;
    }

    public Answer AddAnswer(Guid id, Guid userId, string text, bool isSellerOrStaff, bool isApproved, DateTime createdAtUtc)
    {
        var answer = new Answer(id, Id, userId, text, isSellerOrStaff, isApproved, createdAtUtc);
        _answers.Add(answer);
        return answer;
    }
}

public sealed class Answer : Entity<Guid>
{
    public Guid QuestionId { get; private set; }
    public Guid UserId { get; private set; }
    public bool IsSellerOrStaff { get; private set; }
    public string AnswerText { get; private set; } = string.Empty;
    public bool IsApproved { get; private set; }

    private Answer() { }

    internal Answer(Guid id, Guid questionId, Guid userId, string text, bool isSellerOrStaff, bool isApproved,
        DateTime createdAtUtc) : base(id)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ValidationException("answerText", "Answer text is required.");
        QuestionId = questionId;
        UserId = userId;
        AnswerText = text.Trim();
        IsSellerOrStaff = isSellerOrStaff;
        IsApproved = isApproved;
        CreatedAtUtc = createdAtUtc;
    }
}
