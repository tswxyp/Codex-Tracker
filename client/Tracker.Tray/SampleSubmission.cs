using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

internal enum UploadState { Ready, Checking, CheckUnavailable, Ineligible, Sending, Stored, Unconfirmed, Rejected, Reporting, ReportUnconfirmed, Excluded }

internal sealed class SampleSubmission(CalibrationSample sample)
{
    internal const string Host="codex-tracker-calibration.tswxyp.workers.dev";
    static readonly Uri Endpoint=new("https://"+Host+"/v2/samples");
    static readonly HttpClient Client=new(new SocketsHttpHandler{
        AllowAutoRedirect=false,UseCookies=false,Credentials=null,ActivityHeadersPropagator=null
    }){Timeout=TimeSpan.FromSeconds(20),MaxResponseContentBufferSize=4096};
    internal Guid Receipt {get;}=Guid.NewGuid();
    internal CalibrationSample Sample=>sample;
    internal UploadState State {get;private set;}=UploadState.Ready;
    internal string? Review {get;private set;}
    internal long RetryAt {get;private set;}
    int failures;
    bool deliveryAttempted;
    bool invalidated;
    internal bool NeedsUpload=>State is UploadState.Ready or UploadState.CheckUnavailable or UploadState.Unconfirmed;
    internal bool NeedsRetry=>NeedsUpload || State==UploadState.ReportUnconfirmed;
    sealed record Reply(string Status,Guid Receipt,string? Review);

    internal async Task SendAsync(string database,string root,long consent,TurnSpeeds speeds,CancellationToken token)
    {
        if(!NeedsUpload || token.IsCancellationRequested)return;
        State=UploadState.Checking;
        try{
            bool valid=await Task.Run(()=>sample.StillMatches(database,root,consent,speeds,token),token);
            if(token.IsCancellationRequested)return;
            if(!valid){
                if(deliveryAttempted){invalidated=true;await TransferAsync(true,token);}
                else State=UploadState.Ineligible;
                return;
            }
        }catch(Exception e) when(e is SqliteException or IOException or UnauthorizedAccessException or
            OverflowException or FormatException or OperationCanceledException){
            Retry(UploadState.CheckUnavailable);return;
        }
        deliveryAttempted=true;
        await TransferAsync(false,token);
    }

    void Retry(UploadState state){
        State=state;
        RetryAt=Environment.TickCount64+(30_000L << Math.Min(failures++,5));
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
                cacheWrite=sample.CacheWrite,reasoning=sample.Reasoning,
                points=sample.Window.Points,snapshotTokens=sample.SnapshotTokens,
                activeSessions=sample.ActiveSessions,taskOverlap=sample.TaskOverlap,unattributedUsage=sample.UnattributedUsage};
            var url=report?new Uri(Endpoint.AbsoluteUri+(invalidated?"/invalidate":"/exclude-multitask")):Endpoint;
            using var response=await Client.PostAsJsonAsync(url,body,token);
            if(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created){
                var result=await response.Content.ReadFromJsonAsync<Reply>(token);
                if(result?.Status!="stored" || result.Receipt!=Receipt ||
                    result.Review is not ("approved" or "excluded_uncertain" or "excluded_multitask") ||
                    (report && result.Review=="approved")){
                    Retry(unconfirmed);return;
                }
                failures=0;
                Review=result.Review;
                State=Review is "excluded_multitask" or "excluded_uncertain"?UploadState.Excluded:UploadState.Stored;
            }else{
                if(report && response.StatusCode==HttpStatusCode.NotFound)State=UploadState.Ineligible;
                else if(!report && response.StatusCode is (HttpStatusCode.BadRequest or HttpStatusCode.Conflict or
                    HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity))
                    State=UploadState.Rejected;
                else Retry(unconfirmed);
            }
        }catch(Exception e) when(e is HttpRequestException or IOException or OperationCanceledException or JsonException){
            Retry(unconfirmed);
        }
    }
}
