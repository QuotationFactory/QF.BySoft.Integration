using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using MetalHeaven.Agent.Shared.External.Interfaces;
using MetalHeaven.Agent.Shared.External.Messages;
using MetalHeaven.Integration.Shared.Classes;
using MetalHeaven.Integration.Shared.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using QF.BySoft.Entities;
using QF.BySoft.Integration.Features.AgentOutputFile;
using QF.BySoft.Manufacturability.Interfaces;
using Versioned.ExternalDataContracts;
using Versioned.ExternalDataContracts.Contracts.Resource;
using Versioned.ExternalDataContracts.Enums;
using Constants = QF.BySoft.Entities.Constants;

namespace QF.BySoft.Integration.Features.BySoftIntegration;

/// <summary>
///     BySoft import is used to export information received from Quotation Factory to BySoft.
///     We import the dxf to BySoft. When the dxf is in BySoft the operator can send
///     the dxf directly to the machine.
/// </summary>
/// <remarks>
///     The code in this function is the code that is executed by the agent.
/// </remarks>
public class BySoftIntegration : IBySoftIntegration
{
    private readonly IAgentMessageSerializationHelper _agentMessageSerializationHelper;
    private readonly AgentSettings _agentSettings;
    private readonly BySoftIntegrationSettings _bySoftIntegrationSettings;
    private readonly IBySoftManufacturabilityCheckBending _bySoftManufacturabilityCheckBending;
    private readonly ILogger<BySoftIntegration> _logger;

    public BySoftIntegration(
        ILogger<BySoftIntegration> logger,
        IOptions<AgentSettings> agentSettings,
        IBySoftManufacturabilityCheckBending bySoftManufacturabilityCheckBending,
        IAgentMessageSerializationHelper agentMessageSerializationHelper,
        IOptions<BySoftIntegrationSettings> bySoftIntegrationSettings)
    {
        _logger = logger;
        _agentSettings = agentSettings.Value;
        _bySoftManufacturabilityCheckBending = bySoftManufacturabilityCheckBending;
        _agentMessageSerializationHelper = agentMessageSerializationHelper;
        _bySoftIntegrationSettings = bySoftIntegrationSettings.Value;
    }

    public async Task HandleManufacturabilityCheckRequestAsync(string jsonFilePath)
    {
        RequestManufacturabilityCheckOfPartTypeMessage request = null;
        try
        {
            if (jsonFilePath == null)
            {
                throw new ArgumentNullException(nameof(jsonFilePath));
            }


            // check if json file exists, handle the request
            if (!File.Exists(jsonFilePath))
            {
                throw new ApplicationException($"File (json) not found. Import failed. Expected file: {jsonFilePath}.");
            }

            // define json serializer settings
            var settings = new JsonSerializerSettings();
            settings.SetJsonSettings();
            settings.AddJsonConverters();
            settings.SerializationBinder = new CrossPlatformTypeBinder();

            // read all text from file that is created
            var json = await File.ReadAllTextAsync(jsonFilePath);

            // convert json to project object
            request = JsonConvert.DeserializeObject<RequestManufacturabilityCheckOfPartTypeMessage>(json, settings);
            if (request == null)
            {
                throw new ApplicationException("Deserializing request failed");
            }

            var stepDownloadDirectory = _agentSettings.GetStepDownloadDirectory(Constants.AgentIntegrationName);
            var stepFileUrl = request.StepFileUrl;
            // The name of the step-file depends on the app setting SavePartWithCombinedFileName
            // If false: Step file name will have the same name as the part-id , with the extension .step
            // If true : Step file name will be partId_partName , with the extension .step
            var stepName = _bySoftIntegrationSettings.SavePartWithCombinedFileName
                ? $"{request.PartType.Id.ToString()}_{request.PartType.Name}"
                : request.PartType.Id.ToString();
            var stepFilePathName = Path.Combine(stepDownloadDirectory, $"{stepName}.step");

            // Download the step file sync
            await DownloadStepFileAsync(stepFileUrl, stepFilePathName);
            _logger.LogInformation("Downloaded step file: {StepFilePathName}", stepFilePathName);

            // Do manufacturability check with BySoft CAM API
            var result = await _bySoftManufacturabilityCheckBending.ManufacturabilityCheckBendingAsync(request, stepFilePathName);

            if (result != null)
            {
                var responseJson = _agentMessageSerializationHelper.ToJson(result);

                var fileName = $"{result.PartTypeId.ToString()}.json";
                // get temp file path
                var tempFile = Path.Combine(Path.GetTempPath(), fileName);
                // We save the json first to the temp directory and then copy it to the Agent Input direcotry.
                // Saving directly into the agent triggers somethings the filewatcher before all is completely saved.
                // And thus loosing the response.
                await File.WriteAllTextAsync(tempFile, responseJson);

                // Move the result file in the Agent/Integration/Input folder
                _agentSettings.MoveFileToAgentInput(Constants.AgentIntegrationName, tempFile);

                _logger.LogInformation("Response send. Response file: {ResponseFileName}", fileName);
                _agentSettings.MoveFileToProcessed(Constants.AgentIntegrationName, jsonFilePath);
#if DEBUG
                // Save in InputSend folder, for debugging
                var agentInputHistoryFolder = _agentSettings.GetInputSendDirectory(Constants.AgentIntegrationName);
                var responseFileHistory = Path.Combine(agentInputHistoryFolder, fileName);
                await File.WriteAllTextAsync(responseFileHistory, responseJson);
#endif
            }
            else
            {
                _logger.LogWarning("Did not receive a valid result, could not return result");
                _agentSettings.MoveFileToError(Constants.AgentIntegrationName, jsonFilePath);
            }

            _logger.LogInformation("Finished processing file: {JsonFilePath}", jsonFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file: {JsonFilePath}", jsonFilePath);
            // Do not move the file, but copy, see remark above
            _agentSettings.MoveFileToError(Constants.AgentIntegrationName, jsonFilePath);
            ReportException(ex, request);
        }
    }

    private async Task DownloadStepFileAsync(Uri stepFileUrl, string filePathName)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(stepFileUrl);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var file = new FileStream(filePathName, FileMode.CreateNew);
        await stream.CopyToAsync(file);
    }

    private void ReportException(Exception ex, RequestManufacturabilityCheckOfPartTypeMessage request)
    {
        if (request == null)
        {
            return;
        }

        var response = new RequestManufacturabilityCheckOfPartTypeMessageResponse();
        response.ProjectId = request.ProjectId;
        response.PartTypeId = request.PartType.Id;
        response.WorkingStepKey = new WorkingStepKeyV1(WorkingStepTypeV1.SheetBending, WorkingStepTypeV1.SheetBending);
        response.IsManufacturable = false;
        var logs = new List<EventLog>
        {
            new()
            {
                DateTime = DateTime.UtcNow,
                Message = $"Error. {ex.Message}",
                Level = EventLogLevel.Error,
                ProjectId = request.ProjectId,
                PartTypeId = request.PartType.Id
            }
        };
        response.EventLogs = logs;
        var agentUploadFolder = _agentSettings.GetOrCreateAgentInputDirectory(Constants.AgentIntegrationName, true);
        // Save the result file in the Agent/CADMAN-B/Input folder
        var fileName = $"{response.PartTypeId.ToString()}.json";
        var responseFile = Path.Combine(agentUploadFolder, fileName);
        File.WriteAllText(responseFile, JsonConvert.SerializeObject(response));
    }
}
