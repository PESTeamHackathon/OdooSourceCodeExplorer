namespace Chat.App;

public interface IAnswerGenerator
{
    Task<AnswerDraft> GenerateAsync(AnswerRequest request, CancellationToken cancellationToken = default);
}
