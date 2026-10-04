#include "semiinspect.h"
#include <opencv2/opencv.hpp>
#include <chrono>
#include <cmath>
#include <memory>
#include <vector>

namespace {
struct Engine {
    cv::Mat reference, marker, match, aligned, difference, mask, labels, stats, centroids;
    si_recipe recipe;
};
struct Result { si_summary summary{}; std::vector<si_defect> defects; };
cv::Rect rect(si_rect r) { return {r.x, r.y, r.width, r.height}; }
bool inside(si_rect r, int w, int h) {
    return r.x >= 0 && r.y >= 0 && r.width > 0 && r.height > 0 &&
        static_cast<int64_t>(r.x) + r.width <= w && static_cast<int64_t>(r.y) + r.height <= h;
}
bool frame_valid(const uint8_t* p, int w, int h, int stride) {
    return p && w > 0 && h > 0 && w <= 8192 && h <= 8192 && stride >= w && stride <= 16384;
}
double measure(const cv::Mat& image, cv::Rect roi) {
    double sum = 0; int count = 0;
    for (int y = roi.y + 5; y < roi.y + roi.height - 5; y += 5) {
        const auto* row = image.ptr<uint8_t>(y);
        uint8_t lo = 255, hi = 0;
        for (int x = roi.x; x < roi.x + roi.width; ++x) { lo = std::min(lo, row[x]); hi = std::max(hi, row[x]); }
        if (hi - lo < 60) continue;
        const double level = (static_cast<double>(hi) + lo) * 0.5;
        double left = -1, right = -1;
        for (int x = roi.x; x < roi.x + roi.width - 1; ++x) {
            if (row[x] < level && row[x + 1] >= level && left < 0)
                left = x + (level - row[x]) / (row[x + 1] - row[x]);
            if (row[x] >= level && row[x + 1] < level)
                right = x + (row[x] - level) / (row[x] - row[x + 1]);
        }
        if (left >= 0 && right > left) { sum += right - left; ++count; }
    }
    if (!count) throw std::runtime_error("No measurable edges");
    return sum / count;
}
}

int32_t si_version() { return 1; }
int32_t si_create(const uint8_t* p, int32_t w, int32_t h, int32_t stride, const si_recipe* r, si_engine* out) {
    if (out) *out = nullptr;
    if (!out || !r || !frame_valid(p,w,h,stride) || !inside(r->marker,w,h) ||
        !inside(r->inspection,w,h) || !inside(r->measurement,w,h) || r->max_shift < 0 || r->max_shift > 32 ||
        r->marker.x < r->max_shift || r->marker.y < r->max_shift ||
        r->marker.x + r->marker.width + r->max_shift > w || r->marker.y + r->marker.height + r->max_shift > h ||
        r->threshold <= 0 || r->threshold > 255 || r->min_area < 1 ||
        !std::isfinite(r->min_alignment) || r->min_alignment <= 0 || r->min_alignment > 1 ||
        !std::isfinite(r->min_width) || !std::isfinite(r->max_width) || r->min_width <= 0 || r->max_width < r->min_width ||
        !std::isfinite(r->microns_per_pixel) || r->microns_per_pixel <= 0 || r->measurement.height < 20) return 1;
    try {
        cv::setNumThreads(1);
        auto e = std::make_unique<Engine>();
        e->reference = cv::Mat(h,w,CV_8UC1,const_cast<uint8_t*>(p),stride).clone();
        e->recipe = *r; e->marker = e->reference(rect(r->marker)).clone();
        *out = e.release(); return 0;
    } catch (...) { return 3; }
}
void si_destroy(si_engine e) { delete static_cast<Engine*>(e); }
int32_t si_inspect(si_engine handle, const uint8_t* p, int32_t w, int32_t h, int32_t stride, si_result* out) {
    if (out) *out = nullptr;
    if (!out || !handle || !frame_valid(p,w,h,stride)) return 1;
    auto& e = *static_cast<Engine*>(handle);
    if (w != e.reference.cols || h != e.reference.rows) return 1;
    try {
        const auto begin = std::chrono::steady_clock::now();
        const cv::Mat frame(h,w,CV_8UC1,const_cast<uint8_t*>(p),stride);
        const int s = e.recipe.max_shift;
        const auto m = rect(e.recipe.marker);
        cv::matchTemplate(frame(cv::Rect(m.x-s,m.y-s,m.width+2*s,m.height+2*s)),e.marker,e.match,cv::TM_CCOEFF_NORMED);
        double score; cv::Point location;
        cv::minMaxLoc(e.match,nullptr,&score,nullptr,&location);
        if (!std::isfinite(score) || score < e.recipe.min_alignment) return 2;
        const int dx = location.x-s, dy = location.y-s;
        const cv::Mat transform(cv::Matx23d(1,0,-dx,0,1,-dy));
        cv::warpAffine(frame,e.aligned,transform,frame.size(),cv::INTER_NEAREST,cv::BORDER_CONSTANT,cv::Scalar(40));
        const auto roi = rect(e.recipe.inspection);
        cv::absdiff(e.aligned(roi),e.reference(roi),e.difference);
        cv::threshold(e.difference,e.mask,e.recipe.threshold,255,cv::THRESH_BINARY);
        cv::morphologyEx(e.mask,e.mask,cv::MORPH_CLOSE,cv::Mat::ones(3,3,CV_8UC1));
        const int n = cv::connectedComponentsWithStats(e.mask,e.labels,e.stats,e.centroids);
        auto result = std::make_unique<Result>();
        for (int i = 1; i < n; ++i) {
            const int area = e.stats.at<int>(i,cv::CC_STAT_AREA);
            if (area < e.recipe.min_area) continue;
            result->defects.push_back({e.stats.at<int>(i,cv::CC_STAT_LEFT)+roi.x,
                e.stats.at<int>(i,cv::CC_STAT_TOP)+roi.y,e.stats.at<int>(i,cv::CC_STAT_WIDTH),
                e.stats.at<int>(i,cv::CC_STAT_HEIGHT),area});
        }
        const double width = measure(e.aligned,rect(e.recipe.measurement));
        result->summary = {dx,dy,static_cast<int32_t>(result->defects.size()),
            result->defects.empty() && width >= e.recipe.min_width && width <= e.recipe.max_width ? 1 : 0,
            score,width,width*e.recipe.microns_per_pixel,
            std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-begin).count()};
        *out = result.release(); return 0;
    } catch (...) { return 3; }
}
int32_t si_get_summary(si_result p, si_summary* s) {
    if (!p || !s) return 1; *s = static_cast<Result*>(p)->summary; return 0;
}
int32_t si_get_defect(si_result p, int32_t i, si_defect* d) {
    if (!p || !d || i < 0 || static_cast<size_t>(i) >= static_cast<Result*>(p)->defects.size()) return 1;
    *d = static_cast<Result*>(p)->defects[static_cast<size_t>(i)]; return 0;
}
void si_release_result(si_result p) { delete static_cast<Result*>(p); }
