namespace ReportGenerator.Models.Api
{
    public sealed class ApiError
    {
        public ApiError(string error, string message)
        {
            Error = error;
            Message = message;
        }

        public string Error { get; }
        public string Message { get; }
    }
}
