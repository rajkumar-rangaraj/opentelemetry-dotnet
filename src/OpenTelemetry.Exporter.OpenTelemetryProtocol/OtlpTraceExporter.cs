// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Diagnostics;
using OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation;
using OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer;
using OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Transmission;
using OpenTelemetry.Resources;

namespace OpenTelemetry.Exporter;

/// <summary>
/// Exporter consuming <see cref="Activity"/> and exporting the data using
/// the OpenTelemetry protocol (OTLP).
/// </summary>
public class OtlpTraceExporter : BaseExporter<Activity>
{
    private const int ReserveSizeForLength = 4;
    private const int GrpcStartWritePosition = 5;
    private readonly SdkLimitOptions sdkLimitOptions;
    private readonly OtlpExporterTransmissionHandler transmissionHandler;
    private readonly int startWritePosition;

    private readonly Stack<List<Activity>> activityListPool = [];
    private readonly Dictionary<string, List<Activity>> scopeTracesList = [];

    private Resource? resource;

    // Initial buffer size set to ~732KB.
    // This choice allows us to gradually grow the buffer while targeting a final capacity of around 100 MB,
    // by the 7th doubling to maintain efficient allocation without frequent resizing.
    private byte[] buffer = new byte[750000];

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpTraceExporter"/> class.
    /// </summary>
    /// <param name="options">Configuration options for the export.</param>
    public OtlpTraceExporter(OtlpExporterOptions options)
        : this(options, sdkLimitOptions: new(), experimentalOptions: new(), transmissionHandler: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpTraceExporter"/> class.
    /// </summary>
    /// <param name="exporterOptions"><see cref="OtlpExporterOptions"/>.</param>
    /// <param name="sdkLimitOptions"><see cref="SdkLimitOptions"/>.</param>
    /// <param name="experimentalOptions"><see cref="ExperimentalOptions"/>.</param>
    /// <param name="transmissionHandler"><see cref="OtlpExporterTransmissionHandler"/>.</param>
    internal OtlpTraceExporter(
        OtlpExporterOptions exporterOptions,
        SdkLimitOptions sdkLimitOptions,
        ExperimentalOptions experimentalOptions,
        OtlpExporterTransmissionHandler? transmissionHandler = null)
    {
        Debug.Assert(exporterOptions != null, "exporterOptions was null");
        Debug.Assert(sdkLimitOptions != null, "sdkLimitOptions was null");

        this.sdkLimitOptions = sdkLimitOptions!;
        this.startWritePosition = exporterOptions!.Protocol == OtlpExportProtocol.Grpc ? GrpcStartWritePosition : 0;
        this.transmissionHandler = transmissionHandler ?? exporterOptions!.GetExportTransmissionHandler(experimentalOptions, OtlpSignalType.Traces);
    }

    internal Resource Resource => this.resource ??= this.ParentProvider.GetResource();

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Activity> activityBatch)
    {
        // Prevents the exporter's gRPC and HTTP operations from being instrumented.
        using var scope = SuppressInstrumentationScope.Begin();

        try
        {
            int writePosition = this.WriteTraceData(ref this.buffer, this.startWritePosition, activityBatch, this.Resource);

            if (this.startWritePosition == GrpcStartWritePosition)
            {
                // Grpc payload consists of 3 parts
                // byte 0 - Specifying if the payload is compressed.
                // 1-4 byte - Specifies the length of payload in big endian format.
                // 5 and above -  Protobuf serialized data.
                Span<byte> data = new Span<byte>(this.buffer, 1, 4);
                var dataLength = writePosition - GrpcStartWritePosition;
                BinaryPrimitives.WriteUInt32BigEndian(data, (uint)dataLength);
            }

            if (!this.transmissionHandler.TrySubmitRequest(this.buffer, writePosition))
            {
                return ExportResult.Failure;
            }
        }
        catch (Exception ex)
        {
            OpenTelemetryProtocolExporterEventSource.Log.ExportMethodException(ex);
            return ExportResult.Failure;
        }

        return ExportResult.Success;
    }

    internal int WriteTraceData(ref byte[] buffer, int writePosition, in Batch<Activity> batch, Resource resource)
    {
        writePosition = ProtobufSerializer.WriteTag(buffer, writePosition, ProtobufOtlpTraceFieldNumberConstants.TracesData_Resource_Spans, ProtobufWireType.LEN);
        int resourceSpansScopeSpansLengthPosition = writePosition;
        writePosition += ReserveSizeForLength;

        foreach (var activity in batch)
        {
            var sourceName = activity.Source.Name;
            if (!this.scopeTracesList.TryGetValue(sourceName, out var activities))
            {
                activities = this.activityListPool.Count > 0 ? this.activityListPool.Pop() : [];
                this.scopeTracesList[sourceName] = activities;
            }

            activities.Add(activity);
        }

        writePosition = ProtobufOtlpTraceSerializer.TryWriteResourceSpans(ref buffer, writePosition, this.scopeTracesList, this.sdkLimitOptions, resource);
        this.ReturnActivityListToPool();
        ProtobufSerializer.WriteReservedLength(buffer, resourceSpansScopeSpansLengthPosition, writePosition - (resourceSpansScopeSpansLengthPosition + ReserveSizeForLength));

        return writePosition;
    }

    internal void ReturnActivityListToPool()
    {
        if (this.scopeTracesList.Count != 0)
        {
            foreach (var entry in this.scopeTracesList)
            {
                entry.Value.Clear();
                this.activityListPool.Push(entry.Value);
            }

            this.scopeTracesList.Clear();
        }
    }

    /// <inheritdoc />
    protected override bool OnShutdown(int timeoutMilliseconds) => this.transmissionHandler.Shutdown(timeoutMilliseconds);
}
