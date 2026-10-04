#include "semiinspect.h"
#include <opencv2/opencv.hpp>
#include <gtest/gtest.h>

class EngineTest : public testing::Test {
protected:
    cv::Mat reference;
    si_engine engine=nullptr;
    void SetUp() override {
        reference=cv::Mat(512,512,CV_8UC1,cv::Scalar(40));
        cv::rectangle(reference,{32,32,30,8},cv::Scalar(210),-1);
        cv::rectangle(reference,{32,32,8,32},cv::Scalar(210),-1);
        cv::rectangle(reference,{350,330,32,120},cv::Scalar(200),-1);
        si_recipe r{{25,25,48,48},{120,120,180,180},{330,330,80,120},5,30,9,0.8,30,34,0.5};
        ASSERT_EQ(si_create(reference.data,512,512,512,&r,&engine),0);
    }
    void TearDown() override { si_destroy(engine); }
};
TEST_F(EngineTest, NormalWidthAndUnits) {
    si_result result=nullptr; ASSERT_EQ(si_inspect(engine,reference.data,512,512,512,&result),0);
    si_summary summary{}; ASSERT_EQ(si_get_summary(result,&summary),0);
    EXPECT_EQ(summary.passed,1); EXPECT_NEAR(summary.width_pixels,32,0.01); EXPECT_NEAR(summary.width_microns,16,0.01);
    si_release_result(result);
}
TEST_F(EngineTest, DefectDetectedAndBoundsChecked) {
    cv::Mat frame=reference.clone(); cv::rectangle(frame,{150,150,5,5},cv::Scalar(210),-1);
    si_result result=nullptr; ASSERT_EQ(si_inspect(engine,frame.data,512,512,512,&result),0);
    si_summary s{}; si_get_summary(result,&s); EXPECT_EQ(s.passed,0); EXPECT_EQ(s.defect_count,1);
    si_defect d{}; EXPECT_EQ(si_get_defect(result,0,&d),0); EXPECT_EQ(d.area,25); EXPECT_EQ(si_get_defect(result,1,&d),1);
    si_release_result(result);
}
TEST_F(EngineTest, Translation) {
    cv::Mat frame; cv::Mat t(cv::Matx23d(1,0,5,0,1,-5));
    cv::warpAffine(reference,frame,t,reference.size(),cv::INTER_NEAREST,cv::BORDER_CONSTANT,cv::Scalar(40));
    si_result result=nullptr; ASSERT_EQ(si_inspect(engine,frame.data,512,512,512,&result),0);
    si_summary s{}; si_get_summary(result,&s); EXPECT_EQ(s.shift_x,5); EXPECT_EQ(s.shift_y,-5); EXPECT_EQ(s.passed,1);
    si_release_result(result);
}
TEST_F(EngineTest, InvalidFrameAndAlignmentFailure) {
    si_result r=nullptr; EXPECT_EQ(si_inspect(engine,nullptr,512,512,512,&r),1);
    EXPECT_EQ(si_inspect(engine,reference.data,512,512,500,&r),1);
    cv::Mat blank(512,512,CV_8UC1,cv::Scalar(0)); EXPECT_EQ(si_inspect(engine,blank.data,512,512,512,&r),2); EXPECT_EQ(r,nullptr);
}
