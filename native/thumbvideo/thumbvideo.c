/*
 * thumbvideo: one still frame out of a video, for thumbnails.
 *
 * A deliberately tiny, version-proof surface over FFmpeg's libav*: the caller hands in read/seek callbacks
 * and gets back a tightly packed RGB24 picture. No FFmpeg struct crosses this boundary, so the managed side
 * never has to track FFmpeg's ABI.
 *
 * The frame is the one at `fraction` of the duration (0.5 = the middle): seek to the keyframe before it, then
 * decode forward, within a bounded number of pictures. Only that one group of pictures is read, so a seekable network
 * stream is cheap to thumbnail.
 */
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/cpu.h>
#include <libavutil/dict.h>
#include <libavutil/display.h>
#include <libavutil/error.h>
#include <libavutil/log.h>
#include <libavutil/mathematics.h>
#include <libavutil/mem.h>
#include <libavutil/time.h>
#include <libswscale/swscale.h>

#if defined(_WIN32)
#define TV_API __declspec(dllexport)
#else
#define TV_API __attribute__((visibility("default")))
#endif

#ifndef TV_FFMPEG_TAG
#define TV_FFMPEG_TAG "unknown"
#endif
#ifndef TV_DAV1D_TAG
#define TV_DAV1D_TAG "unknown"
#endif

/* error codes (negative return of tv_frame) */
#define TV_E_ARGUMENT  -1
#define TV_E_CONTAINER -2   /* not a container this build can read */
#define TV_E_NO_VIDEO  -3   /* readable, but there is no video stream in it */
#define TV_E_CODEC     -4   /* video codec is not in this build */
#define TV_E_DECODE    -5   /* no frame could be decoded */
#define TV_E_TIMEOUT   -6
#define TV_E_MEMORY    -7
#define TV_E_IO        -8   /* a caller callback failed */

#define IO_BUFFER        (128 * 1024)
#define MAX_PACKETS      4000   /* packets of any stream read per attempt before giving up */
#define MAX_AFTER_KEY    48     /* video packets fed after the keyframe before forcing the decoder to flush */
#define MAX_WAIT_FOR_KEY 300    /* video packets skipped while waiting for a keyframe flag */
#define MAX_FORWARD      900    /* video packets decoded past the keyframe on the way to the wanted time */
#define MIN_FORWARD      25
#define FORWARD_PIXELS   1000000000LL   /* ... fewer for larger pictures: that walk decodes at most this many pixels.
                                           A count, not a clock, so the same input always gives the same frame. */
#define MAX_THREADS      4
#define MAX_SIDE         16384
#define MAX_PIXELS       (8192 * 4352)   /* per frame, checked by the decoders before they allocate: 8K DCI fits, a
                                           crafted 16000x16000 header (GBs of reference frames) does not */

typedef int (*tv_read)(void *opaque, uint8_t *buffer, int size);         /* bytes read, 0 = end, <0 = error */
typedef int64_t (*tv_seek)(void *opaque, int64_t offset, int whence);    /* whence 0/1/2 = set/cur/end, 0x10000 = report size; <0 = error */

typedef struct tv_info {
    int32_t width, height;      /* of the returned picture (sample aspect ratio applied, not yet rotated) */
    int32_t rotation;           /* clockwise degrees the caller still has to apply: 0, 90, 180 or 270 */
    int32_t reserved;
    double duration;            /* seconds, < 0 when unknown */
    double frame_time;          /* seconds from the start of the returned frame, < 0 when unknown */
    char codec[32];
    char container[32];
} tv_info;

typedef struct State {
    tv_read read;
    tv_seek seek;
    void *opaque;
    int io_failed;
    int64_t deadline;           /* av_gettime_relative() value, 0 = none */
    AVFormatContext *format;
    AVCodecContext *decoder;
    AVPacket *packet;
    AVFrame *scratch;
    int stream;
} State;

static int io_read(void *opaque, uint8_t *buffer, int size)
{
    State *s = opaque;
    int n = s->read(s->opaque, buffer, size);
    if (n > 0) return n;
    if (n == 0) return AVERROR_EOF;
    s->io_failed = 1;
    return AVERROR(EIO);
}

static int64_t io_seek(void *opaque, int64_t offset, int whence)
{
    State *s = opaque;
    int64_t position;
    whence &= ~AVSEEK_FORCE;
    position = s->seek(s->opaque, offset, whence);
    if (position < 0) {
        if (whence != AVSEEK_SIZE) s->io_failed = 1;
        return AVERROR(EIO);
    }
    return position;
}

static int expired(void *opaque)
{
    State *s = opaque;
    return s->deadline && av_gettime_relative() > s->deadline;
}

static void fail(char *error, int size, const char *message, int averror)
{
    char detail[128];
    if (!error || size <= 0) return;
    if (averror < 0 && av_strerror(averror, detail, sizeof(detail)) == 0) snprintf(error, size, "%s (%s)", message, detail);
    else snprintf(error, size, "%s", message);
}

static int reached(const AVFrame *frame, int64_t target)
{
    return target == AV_NOPTS_VALUE || frame->best_effort_timestamp == AV_NOPTS_VALUE || frame->best_effort_timestamp >= target;
}

/*
 * Decodes from the current position: waits for a keyframe, feeds it and what follows until a picture comes out,
 * then keeps decoding towards `target` (stream time base; AV_NOPTS_VALUE = the first picture will do).
 * The walk forward is bounded by the number of pictures (fewer for larger ones), not by a clock: when the bound is
 * reached the latest picture is returned, so a long group of pictures in a heavy video costs a bounded amount of work,
 * still yields a frame past the keyframe, and the same input always gives the same frame.
 */
static int grab(State *s, AVFrame *frame, int64_t target)
{
    int read = 0, waited = 0, fed = 0, failures = 0, started = 0, have = 0, forward = 0, ret;
    int64_t pixels = (int64_t)s->decoder->width * s->decoder->height, limit = MAX_FORWARD;

    if (pixels > 0 && FORWARD_PIXELS / pixels < limit) limit = FORWARD_PIXELS / pixels;
    if (limit < MIN_FORWARD) limit = MIN_FORWARD;

    avcodec_flush_buffers(s->decoder);
    for (;;) {
        if (expired(s)) return have ? 0 : AVERROR(ETIMEDOUT);
        ret = av_read_frame(s->format, s->packet);
        if (ret < 0) break;                                   /* end of data: flush what the decoder holds */
        if (++read > MAX_PACKETS) { av_packet_unref(s->packet); break; }
        if (s->packet->stream_index != s->stream) { av_packet_unref(s->packet); continue; }
        if (!started && !(s->packet->flags & AV_PKT_FLAG_KEY) && ++waited < MAX_WAIT_FOR_KEY) { av_packet_unref(s->packet); continue; }
        started = 1;

        ret = avcodec_send_packet(s->decoder, s->packet);
        av_packet_unref(s->packet);
        while (ret >= 0 || ret == AVERROR(EAGAIN)) {
            ret = avcodec_receive_frame(s->decoder, s->scratch);
            if (ret < 0) break;
            av_frame_unref(frame);
            av_frame_move_ref(frame, s->scratch);
            have = 1;
            if (reached(frame, target)) return 0;
            ret = 0;
        }
        if (have) {
            if (ret != AVERROR(EAGAIN)) return 0;             /* damaged further on: what we have is good */
            if (++forward > limit) return 0;
            continue;
        }
        if (ret != AVERROR(EAGAIN)) {                         /* damaged data: start over at the next keyframe */
            if (++failures > 8) return ret;
            avcodec_flush_buffers(s->decoder);
            started = 0; waited = 0; fed = 0;
            continue;
        }
        if (++fed >= MAX_AFTER_KEY) break;
    }
    if (!started) return AVERROR_INVALIDDATA;
    avcodec_send_packet(s->decoder, NULL);
    while (avcodec_receive_frame(s->decoder, s->scratch) >= 0) {
        av_frame_unref(frame);
        av_frame_move_ref(frame, s->scratch);
        have = 1;
        if (reached(frame, target)) break;
    }
    return have ? 0 : AVERROR_INVALIDDATA;
}

/* Clockwise quarter turns still to be applied, from the display matrix of the frame or of the stream. */
static int rotation_of(const AVStream *stream, const AVFrame *frame)
{
    const int32_t *matrix = NULL;
    const AVFrameSideData *frame_data = av_frame_get_side_data(frame, AV_FRAME_DATA_DISPLAYMATRIX);
    double degrees;
    int quarter;

    if (frame_data && frame_data->size >= 9 * sizeof(int32_t)) matrix = (const int32_t *)frame_data->data;
    else {
        const AVPacketSideData *stream_data = av_packet_side_data_get(stream->codecpar->coded_side_data,
            stream->codecpar->nb_coded_side_data, AV_PKT_DATA_DISPLAYMATRIX);
        if (stream_data && stream_data->size >= 9 * sizeof(int32_t)) matrix = (const int32_t *)stream_data->data;
    }
    if (!matrix) return 0;
    degrees = -av_display_rotation_get(matrix);               /* the matrix is counter-clockwise */
    if (degrees != degrees) return 0;                         /* NaN: singular matrix */
    quarter = (int)lrint(degrees / 90.0) % 4;
    if (quarter < 0) quarter += 4;
    return quarter * 90;
}

static int to_rgb(State *s, const AVStream *stream, const AVFrame *frame, tv_info *info, uint8_t **rgb)
{
    int width = frame->width, height = frame->height, out_width = width, colorspace, full_range;
    AVRational aspect = av_guess_sample_aspect_ratio(s->format, (AVStream *)stream, (AVFrame *)frame);
    struct SwsContext *scaler;
    uint8_t *pixels;
    uint8_t *planes[4] = { 0 };
    int strides[4] = { 0 };

    if (width <= 0 || height <= 0 || width > MAX_SIDE || height > MAX_SIDE) return TV_E_DECODE;
    if (aspect.num > 0 && aspect.den > 0 && aspect.num != aspect.den) {
        double ratio = av_q2d(aspect);
        if (ratio > 0.2 && ratio < 5.0) out_width = (int)lrint(width * ratio);   /* anamorphic: display width */
        if (out_width < 1 || out_width > MAX_SIDE) out_width = width;
    }

    scaler = sws_getContext(width, height, frame->format, out_width, height, AV_PIX_FMT_RGB24,
        SWS_BICUBIC | SWS_ACCURATE_RND | SWS_FULL_CHR_H_INT, NULL, NULL, NULL);
    if (!scaler) return TV_E_DECODE;

    /* untagged video: HD is BT.709, everything smaller BT.601, as every player assumes */
    colorspace = frame->colorspace;
    if (colorspace == AVCOL_SPC_UNSPECIFIED || colorspace == AVCOL_SPC_RGB) colorspace = height >= 720 ? AVCOL_SPC_BT709 : AVCOL_SPC_SMPTE170M;
    full_range = frame->color_range == AVCOL_RANGE_JPEG;
    sws_setColorspaceDetails(scaler, sws_getCoefficients(colorspace), full_range, sws_getCoefficients(SWS_CS_DEFAULT), 1, 0, 1 << 16, 1 << 16);

    pixels = malloc((size_t)out_width * height * 3 + 64);
    if (!pixels) { sws_freeContext(scaler); return TV_E_MEMORY; }
    planes[0] = pixels;
    strides[0] = out_width * 3;
    if (sws_scale(scaler, (const uint8_t *const *)frame->data, frame->linesize, 0, height, planes, strides) <= 0) {
        sws_freeContext(scaler);
        free(pixels);
        return TV_E_DECODE;
    }
    sws_freeContext(scaler);

    info->width = out_width;
    info->height = height;
    info->rotation = rotation_of(stream, frame);
    *rgb = pixels;
    return 0;
}

/* One complete pass: open, pick the video stream, go to `fraction` of the duration (0 = do not seek at all), decode, convert. */
static int attempt(tv_read read, tv_seek seek, void *opaque, double fraction, int64_t deadline,
                   tv_info *info, uint8_t **rgb, char *error, int error_size)
{
    State s = { 0 };
    AVIOContext *io = NULL;
    AVFrame *frame = NULL;
    const AVCodec *codec = NULL;
    AVStream *stream;
    uint8_t *buffer;
    int64_t start, duration, size, target = AV_NOPTS_VALUE;
    int ret, result = TV_E_DECODE, positioned = 1, threads;
    unsigned i;

    memset(info, 0, sizeof(*info));
    info->duration = info->frame_time = -1;
    s.read = read; s.seek = seek; s.opaque = opaque; s.stream = -1; s.deadline = deadline;

    buffer = av_malloc(IO_BUFFER);
    io = buffer ? avio_alloc_context(buffer, IO_BUFFER, 0, &s, io_read, NULL, io_seek) : NULL;
    s.format = avformat_alloc_context();
    s.packet = av_packet_alloc();
    frame = av_frame_alloc();
    s.scratch = av_frame_alloc();
    if (!io || !s.format || !s.packet || !frame || !s.scratch) {
        if (!io) av_free(buffer);
        fail(error, error_size, "out of memory", 0);
        result = TV_E_MEMORY;
        goto done;
    }
    s.format->pb = io;
    s.format->flags |= AVFMT_FLAG_CUSTOM_IO;
    s.format->interrupt_callback.callback = expired;
    s.format->interrupt_callback.opaque = &s;

    ret = avformat_open_input(&s.format, NULL, NULL, NULL);    /* frees the context itself on failure */
    if (ret < 0) { fail(error, error_size, "not a readable video container", ret); result = TV_E_CONTAINER; goto done; }
    snprintf(info->container, sizeof(info->container), "%s", s.format->iformat->name);
    /* find_stream_info decodes frames too: the same pixel limit for its decoders */
    {
        unsigned count = s.format->nb_streams;
        AVDictionary **limits = count ? av_calloc(count, sizeof(*limits)) : NULL;
        if (count && !limits) { fail(error, error_size, "out of memory", 0); result = TV_E_MEMORY; goto done; }
        for (i = 0; i < count; i++) av_dict_set_int(&limits[i], "max_pixels", MAX_PIXELS, 0);
        ret = avformat_find_stream_info(s.format, limits);    /* streams it adds on the way get the defaults */
        for (i = 0; i < count; i++) av_dict_free(&limits[i]);
        av_free(limits);
    }
    if (ret < 0) { fail(error, error_size, "cannot read the stream layout", ret); result = TV_E_CONTAINER; goto done; }

    ret = av_find_best_stream(s.format, AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
    if (ret == AVERROR_DECODER_NOT_FOUND) {
        char message[96];
        const char *name = "unknown";
        for (i = 0; i < s.format->nb_streams; i++)
            if (s.format->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) { name = avcodec_get_name(s.format->streams[i]->codecpar->codec_id); break; }
        snprintf(info->codec, sizeof(info->codec), "%s", name);
        snprintf(message, sizeof(message), "video codec '%s' is not supported", name);
        fail(error, error_size, message, 0);
        result = TV_E_CODEC;
        goto done;
    }
    if (ret < 0) { fail(error, error_size, "the file has no video stream", 0); result = TV_E_NO_VIDEO; goto done; }
    s.stream = ret;
    stream = s.format->streams[s.stream];
    snprintf(info->codec, sizeof(info->codec), "%s", codec->name);
    for (i = 0; i < s.format->nb_streams; i++)                /* demuxers skip the bytes of discarded streams */
        if ((int)i != s.stream) s.format->streams[i]->discard = AVDISCARD_ALL;

    if ((int64_t)stream->codecpar->width * stream->codecpar->height > MAX_PIXELS) {
        fail(error, error_size, "video frame is too large", 0);
        goto done;
    }
    s.decoder = avcodec_alloc_context3(codec);
    if (!s.decoder) { fail(error, error_size, "out of memory", 0); result = TV_E_MEMORY; goto done; }
    if ((ret = avcodec_parameters_to_context(s.decoder, stream->codecpar)) < 0) { fail(error, error_size, "bad codec parameters", ret); goto done; }
    s.decoder->pkt_timebase = stream->time_base;
    threads = av_cpu_count();
    s.decoder->thread_count = threads < 1 ? 1 : threads > MAX_THREADS ? MAX_THREADS : threads;
    s.decoder->max_pixels = MAX_PIXELS;                       /* the stream header can lie: the decoder checks every frame */
    s.decoder->skip_frame = AVDISCARD_NONREF;                /* pictures nothing depends on are not worth decoding on the way */
    if ((ret = avcodec_open2(s.decoder, codec, NULL)) < 0) { fail(error, error_size, "cannot start the video decoder", ret); goto done; }

    start = s.format->start_time != AV_NOPTS_VALUE ? s.format->start_time : 0;
    duration = s.format->duration != AV_NOPTS_VALUE ? s.format->duration : 0;
    if (duration > 0) info->duration = duration / (double)AV_TIME_BASE;

    if (stream->disposition & AV_DISPOSITION_ATTACHED_PIC) {
        /* cover art only: the picture is the whole stream */
        ret = AVERROR_INVALIDDATA;
        if (stream->attached_pic.size > 0 && avcodec_send_packet(s.decoder, &stream->attached_pic) >= 0) {
            avcodec_send_packet(s.decoder, NULL);
            ret = avcodec_receive_frame(s.decoder, frame);
        }
    } else {
        /* fraction 0 reads on from where the demuxer already is, the very first packet: no seek that could miss it */
        if (fraction > 0) {
            if (duration > 0) {
                double at = (double)start + duration * fraction;   /* both from the file: no int64 overflow */
                int64_t wanted = at > 9e18 ? INT64_MAX / 2 : at < -9e18 ? 0 : (int64_t)at;
                positioned = av_seek_frame(s.format, -1, wanted, AVSEEK_FLAG_BACKWARD) >= 0;
                target = av_rescale_q(wanted, AV_TIME_BASE_Q, stream->time_base);
            } else if (!(s.format->iformat->flags & AVFMT_NO_BYTE_SEEK) && (size = avio_size(io)) > 0)
                positioned = av_seek_frame(s.format, -1, (int64_t)(size * fraction), AVSEEK_FLAG_BYTE) >= 0;
            else
                positioned = 0;
        }
        ret = positioned ? grab(&s, frame, target) : AVERROR_INVALIDDATA;
    }

    if (ret < 0) {
        if (s.io_failed) { fail(error, error_size, "reading the input failed", 0); result = TV_E_IO; }
        else if (ret == AVERROR(ETIMEDOUT) || expired(&s)) { fail(error, error_size, "timed out", 0); result = TV_E_TIMEOUT; }
        else { fail(error, error_size, "no video frame could be decoded", ret); result = TV_E_DECODE; }
        goto done;
    }

    if (frame->best_effort_timestamp != AV_NOPTS_VALUE) {
        double seconds = frame->best_effort_timestamp * av_q2d(stream->time_base) - start / (double)AV_TIME_BASE;
        info->frame_time = seconds < 0 ? 0 : seconds;
    }
    result = to_rgb(&s, stream, frame, info, rgb);
    if (result == TV_E_MEMORY) fail(error, error_size, "out of memory", 0);
    else if (result < 0) fail(error, error_size, "cannot convert the frame to RGB", 0);

done:
    av_frame_free(&frame);
    av_frame_free(&s.scratch);
    av_packet_free(&s.packet);
    avcodec_free_context(&s.decoder);
    if (s.format) avformat_close_input(&s.format);
    if (io) { av_freep(&io->buffer); avio_context_free(&io); }
    return result;
}

TV_API int tv_frame(tv_read read, tv_seek seek, void *opaque, double fraction, int timeout_ms,
                    tv_info *info, uint8_t **rgb, char *error, int error_size)
{
    int64_t deadline = 0;
    int result;

    if (error && error_size > 0) error[0] = 0;
    if (!read || !seek || !info || !rgb) { fail(error, error_size, "invalid argument", 0); return TV_E_ARGUMENT; }
    *rgb = NULL;
    if (!(fraction >= 0)) fraction = 0;
    if (fraction > 0.98) fraction = 0.98;
    av_log_set_level(getenv("THUMBVIDEO_LOG") ? AV_LOG_VERBOSE : AV_LOG_QUIET);   /* libav diagnostics on stderr, for a file that will not open */
    if (timeout_ms > 0) deadline = av_gettime_relative() + (int64_t)timeout_ms * 1000;

    result = attempt(read, seek, opaque, fraction, deadline, info, rgb, error, error_size);
    if (result == TV_E_DECODE && fraction > 0 && seek(opaque, 0, SEEK_SET) == 0) {
        /* nothing decodable around the wanted time (truncated file, no keyframe after it, a container that
           cannot seek): start over and take the first frame, read straight from the top without any seek */
        if (error && error_size > 0) error[0] = 0;
        result = attempt(read, seek, opaque, 0, deadline, info, rgb, error, error_size);
    }
    return result;
}

TV_API void tv_free(uint8_t *rgb)
{
    free(rgb);
}

TV_API const char *tv_version(void)
{
    return "thumbvideo 1, FFmpeg " TV_FFMPEG_TAG ", dav1d " TV_DAV1D_TAG;
}
