using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

internal enum UploadState { Ready, Checking, CheckUnavailable, Ineligible, Sending, Stored, Unconfirmed, Rejected, Reporting, ReportUnconfirmed, Excluded }

internal sealed class SampleSubmission(CalibrationSample sample)
{
    internal const string Host="codex-tracker-calibration.tswxyp.workers.dev";
    static readonly Uri Endpoint=new("https://"+Host+"/v1/samples");
    static readonly HttpClient Client=new(new SocketsHttpHandler{
        AllowAutoRedirect=false,UseCookies=false,Credentials=null,ActivityHeadersPropagator=null
    }){Timeout=TimeSpan.FromSeconds(20),MaxResponseContentBufferSize=4096};
    internal Guid Receipt {get;}=Guid.NewGuid();
    internal UploadState State {get;private set;}=UploadState.Ready;
    internal string? Review {get;private set;}
    sealed record Reply(string Status,Guid Receipt,string? Review);

    internal async Task SendAsync(string database,string root,TurnSpeeds speeds,CancellationToken token)
    {
        if(State is not (UploadState.Ready or UploadState.Unconfirmed or UploadState.CheckUnavailable) || token.IsCancellationRequested)return;
        State=UploadState.Checking;
        try{
            bool valid=await Task.Run(()=>sample.StillMatches(database,root,speeds,token),token);
            if(!valid){State=UploadState.Ineligible;return;}
            if(token.IsCancellationRequested)return;
        }catch(Exception e) when(e is SqliteException or IOException or UnauthorizedAccessException or
            OverflowException or FormatException or OperationCanceledException){
            State=UploadState.CheckUnavailable;return;
        }
        await TransferAsync(false,token);
    }

    internal Task ReportMultitaskingAsync(CancellationToken token)=>
        State is (UploadState.Stored or UploadState.ReportUnconfirmed) && !token.IsCancellationRequested?
            TransferAsync(true,token):Task.CompletedTask;

    async Task TransferAsync(bool report,CancellationToken token)
    {
        State=report?UploadState.Reporting:UploadState.Sending;
        var unconfirmed=report?UploadState.ReportUnconfirmed:UploadState.Unconfirmed;
        try{
            // Keep the wire contract separate from local records. A retry uses the same receipt.
            var body=new{receipt=Receipt,model=sample.Model,effort=sample.Effort,speed=sample.Speed,
                input=sample.Input,output=sample.Output,cacheRead=sample.CacheRead,
                cacheWrite=sample.CacheWrite,reasoning=sample.Reasoning};
            var url=report?new Uri(Endpoint.AbsoluteUri+"/exclude-multitask"):Endpoint;
            using var response=await Client.PostAsJsonAsync(url,body,token);
            if(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created){
                var result=await response.Content.ReadFromJsonAsync<Reply>(token);
                if(result?.Status!="stored" || result.Receipt!=Receipt || (report && result.Review!="excluded_multitask")){
                    State=unconfirmed;return;
                }
                Review=result.Review;
                State=Review is "excluded_multitask" or "excluded_uncertain"?UploadState.Excluded:UploadState.Stored;
            }else{
                State=!report && response.StatusCode is (HttpStatusCode.BadRequest or HttpStatusCode.Conflict or
                    HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity)
                    ?UploadState.Rejected:unconfirmed;
            }
        }catch(Exception e) when(e is HttpRequestException or IOException or OperationCanceledException or JsonException){
            State=unconfirmed;
        }
    }
}
