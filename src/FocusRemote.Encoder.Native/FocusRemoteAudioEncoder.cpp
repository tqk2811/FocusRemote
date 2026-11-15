// Opus audio encoder (FFmpeg libopus, fallback to FFmpeg's native "opus"): interleaved float PCM -> raw Opus packets.
#include "pch.h"
#include "FocusRemoteEncoder.h"

extern "C"
{
#include <libavutil/channel_layout.h>
#include <libavutil/opt.h>
}

namespace
{
	std::string AvError(int code)
	{
		char buffer[AV_ERROR_MAX_STRING_SIZE] = {};
		av_strerror(code, buffer, sizeof(buffer));
		return buffer;
	}
}

struct FrAudioEncoder
{
public:
	~FrAudioEncoder() { Close(); }

	int32_t Open(const FrAudioEncoderOptions* options)
	{
		Close();
		if (!options || options->sampleRate <= 0 || options->channels < 1 || options->channels > 2 || options->bitrateKbps <= 0 || options->frameDurationMs <= 0)
			return Fail("invalid audio encoder options");

		std::string errors;
		for (const char* name : { "libopus", "opus" })
		{
			if (TryOpen(name, options))
				return 0;
			errors += std::string(name) + ": " + _lastError + "; ";
		}
		return Fail("no Opus encoder could be opened (" + errors + ")");
	}

	int32_t FrameSamples() const { return _frameSamples; }
	const char* EncoderName() const { return _encoderName.c_str(); }
	const char* LastError() const { return _lastError.c_str(); }

	int32_t Send(const float* samples, int32_t samplesPerChannel, int64_t timestampUs)
	{
		if (!_codec)
			return Fail("audio encoder is not open");
		if (!samples || samplesPerChannel != _frameSamples)
			return Fail("audio frame must hold exactly " + std::to_string(_frameSamples) + " samples per channel");

		_frame->nb_samples = samplesPerChannel;
		int ret = av_frame_make_writable(_frame);
		if (ret < 0)
			return Fail("av_frame_make_writable: " + AvError(ret));
		memcpy(_frame->data[0], samples, sizeof(float) * samplesPerChannel * _codec->ch_layout.nb_channels);
		_frame->pts = timestampUs;
		ret = avcodec_send_frame(_codec, _frame);
		if (ret < 0)
			return Fail("avcodec_send_frame: " + AvError(ret));
		return 0;
	}

	int32_t Receive(FrPacket* outPacket)
	{
		*outPacket = {};
		if (!_codec)
			return Fail("audio encoder is not open");
		av_packet_unref(_packet);
		int ret = avcodec_receive_packet(_codec, _packet);
		if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF)
			return 0;
		if (ret < 0)
			return Fail("avcodec_receive_packet: " + AvError(ret));
		outPacket->data = _packet->data;
		outPacket->size = _packet->size;
		outPacket->isKeyframe = 1;
		outPacket->pts = _packet->pts;
		return 1;
	}

private:
	bool TryOpen(const char* name, const FrAudioEncoderOptions* options)
	{
		const AVCodec* codec = avcodec_find_encoder_by_name(name);
		if (!codec)
		{
			Fail("not in this FFmpeg build");
			return false;
		}
		_codec = avcodec_alloc_context3(codec);
		if (!_codec)
		{
			Fail("avcodec_alloc_context3 failed");
			return false;
		}
		_codec->sample_fmt = AV_SAMPLE_FMT_FLT;
		_codec->sample_rate = options->sampleRate;
		av_channel_layout_default(&_codec->ch_layout, options->channels);
		_codec->bit_rate = (int64_t)options->bitrateKbps * 1000;
		// pts/timestamps in microseconds, like the video stream.
		_codec->time_base = AVRational{ 1, 1000000 };
		_codec->flags |= AV_CODEC_FLAG_LOW_DELAY;
		_codec->strict_std_compliance = FF_COMPLIANCE_EXPERIMENTAL; // FFmpeg's native "opus" encoder is experimental

		AVDictionary* opts = nullptr;
		if (strcmp(name, "libopus") == 0)
		{
			av_dict_set(&opts, "application", "lowdelay", 0);
			av_dict_set(&opts, "frame_duration", std::to_string(options->frameDurationMs).c_str(), 0);
			av_dict_set(&opts, "vbr", "on", 0);
		}
		else
		{
			av_dict_set(&opts, "opus_delay", std::to_string(options->frameDurationMs).c_str(), 0);
		}
		int ret = avcodec_open2(_codec, codec, &opts);
		av_dict_free(&opts);
		if (ret < 0)
		{
			Fail("avcodec_open2: " + AvError(ret));
			Close();
			return false;
		}

		_frameSamples = _codec->frame_size > 0 ? _codec->frame_size : options->sampleRate * options->frameDurationMs / 1000;
		_frame = av_frame_alloc();
		_packet = av_packet_alloc();
		if (!_frame || !_packet)
		{
			Fail("av_frame_alloc/av_packet_alloc failed");
			Close();
			return false;
		}
		_frame->format = _codec->sample_fmt;
		_frame->sample_rate = _codec->sample_rate;
		_frame->nb_samples = _frameSamples;
		av_channel_layout_copy(&_frame->ch_layout, &_codec->ch_layout);
		ret = av_frame_get_buffer(_frame, 0);
		if (ret < 0)
		{
			Fail("av_frame_get_buffer: " + AvError(ret));
			Close();
			return false;
		}
		_encoderName = name;
		return true;
	}

	void Close()
	{
		av_frame_free(&_frame);
		av_packet_free(&_packet);
		avcodec_free_context(&_codec);
		_frameSamples = 0;
		_encoderName.clear();
	}

	int32_t Fail(const std::string& message)
	{
		_lastError = message;
		return -1;
	}

	AVCodecContext* _codec = nullptr;
	AVFrame* _frame = nullptr;
	AVPacket* _packet = nullptr;
	int32_t _frameSamples = 0;
	std::string _encoderName;
	std::string _lastError;
};

FR_API FrAudioEncoder* FR_CALL FrAudioEncoder_Create()
{
	return new (std::nothrow) FrAudioEncoder();
}

FR_API int32_t FR_CALL FrAudioEncoder_Open(FrAudioEncoder* encoder, const FrAudioEncoderOptions* options)
{
	return encoder ? encoder->Open(options) : -1;
}

FR_API int32_t FR_CALL FrAudioEncoder_GetFrameSamples(FrAudioEncoder* encoder)
{
	return encoder ? encoder->FrameSamples() : 0;
}

FR_API int32_t FR_CALL FrAudioEncoder_Send(FrAudioEncoder* encoder, const float* samples, int32_t samplesPerChannel, int64_t timestampUs)
{
	return encoder ? encoder->Send(samples, samplesPerChannel, timestampUs) : -1;
}

FR_API int32_t FR_CALL FrAudioEncoder_Receive(FrAudioEncoder* encoder, FrPacket* outPacket)
{
	if (!encoder || !outPacket)
		return -1;
	return encoder->Receive(outPacket);
}

FR_API const char* FR_CALL FrAudioEncoder_GetEncoderName(FrAudioEncoder* encoder)
{
	return encoder ? encoder->EncoderName() : "";
}

FR_API const char* FR_CALL FrAudioEncoder_GetLastError(FrAudioEncoder* encoder)
{
	return encoder ? encoder->LastError() : "encoder is null";
}

FR_API void FR_CALL FrAudioEncoder_Destroy(FrAudioEncoder* encoder)
{
	delete encoder;
}
