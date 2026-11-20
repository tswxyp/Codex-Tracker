using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodexTracker.Tray;

internal enum UploadState { Ready, Sending, Stored, Unconfirmed, Rejected }

internal sealed class SampleSubmission(CalibrationSample sample)
{
    internal const string Host="codex-tracker-calibration.tswxyp.workers.dev";
    static readonly Uri Endpoint=new("https://"+Host+"/v1/samples");
    static readonly HttpClient Client=new(new SocketsHttpHandler{
        AllowAutoRedirect=false,UseCookies=false,Credentials=null,ActivityHeadersPropagator=null
    }){Timeout=TimeSpan.FromSeconds(20),MaxResponseContentBufferSize=4096};
    internal Guid Receipt {get;}=Guid.NewGuid();
    internal UploadState State {get;private set;}=UploadState.Ready;
    sealed record Reply(string Status,Guid Receipt);

    internal async Task SendAsync(CancellationToken token)
    {
        if(State is not (UploadState.Ready or UploadState.Unconfirmed) || token.IsCancellationRequested)return;
        State=UploadState.Sending;
        try{
            // Keep the wire contract separate from local records. A retry uses the same receipt.
            var body=new{receipt=Receipt,model=sample.Model,effort=sample.Effort,speed=sample.Speed,
                input=sample.Input,output=sample.Output,cacheRead=sample.CacheRead,
                cacheWrite=sample.CacheWrite,reasoning=sample.Reasoning};
            using var response=await Client.PostAsJsonAsync(Endpoint,body,token);
            if(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created){
                var result=await response.Content.ReadFromJsonAsync<Reply>(token);
                State=result?.Status=="stored" && result.Receipt==Receipt?UploadState.Stored:UploadState.Unconfirmed;
            }else{
                State=response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or
                    HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity
                    ?UploadState.Rejected:UploadState.Unconfirmed;
            }
        }catch(Exception e) when(e is HttpRequestException or IOException or OperationCanceledException or JsonException){
            State=UploadState.Unconfirmed;
        }
    }
}
