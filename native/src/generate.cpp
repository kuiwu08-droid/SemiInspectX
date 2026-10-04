#include <opencv2/opencv.hpp>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <random>

// Ground truth comes from drawing geometry, independently of the detector.
int main(int argc, char** argv) {
    try {
        if (argc < 2) { std::cerr << "si_generate OUTPUT [COUNT=100] [SEED=42] [NOISE=2]\n"; return 1; }
        const std::filesystem::path dir(argv[1]);
        const int count = argc > 2 ? std::stoi(argv[2]) : 100;
        const unsigned seed = argc > 3 ? static_cast<unsigned>(std::stoul(argv[3])) : 42;
        const double noise = argc > 4 ? std::stod(argv[4]) : 2;
        if (count < 1 || count > 10000 || noise < 0 || noise > 2) return 1;
        std::filesystem::create_directories(dir);
        cv::Mat golden(512,512,CV_8UC1,cv::Scalar(40));
        cv::rectangle(golden,{32,32,30,8},cv::Scalar(210),-1);
        cv::rectangle(golden,{32,32,8,32},cv::Scalar(210),-1);
        cv::circle(golden,{61,61},5,cv::Scalar(150),-1);
        for (int x = 132; x < 292; x += 24) cv::rectangle(golden,{x,120,8,180},cv::Scalar(180),-1);
        cv::rectangle(golden,{350,330,32,120},cv::Scalar(200),-1);
        cv::imwrite((dir/"reference.pgm").string(),golden);
        std::mt19937 rng(seed);
        std::uniform_int_distribution<int> offset(-5,5), light(-10,10), spot(0,5);
        std::normal_distribution<double> gaussian(0,noise > 0 ? noise : 1);
        std::ofstream manifest(dir/"manifest.json");
        manifest << "{\"seed\":" << seed << ",\"noiseSigma\":" << noise << ",\"frames\":[\n";
        for (int i=0;i<count;++i) {
            cv::Mat image = golden.clone(), mask(512,512,CV_8UC1,cv::Scalar(0));
            const int kind = i%10, dx=offset(rng), dy=offset(rng), brightness=light(rng);
            int width=32; cv::Rect defect;
            std::string name="normal";
            if (kind==5) { name="particle"; defect={144+24*spot(rng),160+spot(rng)*15,5,5}; cv::rectangle(image,defect,cv::Scalar(220),-1); }
            if (kind==6) { name="scratch"; defect={140,180+spot(rng)*10,42,3}; cv::rectangle(image,defect,cv::Scalar(250),-1); }
            if (kind==7) { name="missing-pattern"; defect={132+24*spot(rng),160,8,20}; cv::rectangle(image,defect,cv::Scalar(40),-1); }
            if (kind==8 || kind==9) {
                name="width-out-of-spec"; width=kind==8?38:26;
                cv::rectangle(image,{330,330,80,120},cv::Scalar(40),-1);
                cv::rectangle(image,{350,330,width,120},cv::Scalar(200),-1);
            }
            if (defect.area()>0) cv::rectangle(mask,defect,cv::Scalar(255),-1);
            const cv::Mat transform(cv::Matx23d(1,0,dx,0,1,dy));
            cv::warpAffine(image,image,transform,image.size(),cv::INTER_NEAREST,cv::BORDER_CONSTANT,cv::Scalar(40));
            cv::warpAffine(mask,mask,transform,mask.size(),cv::INTER_NEAREST,cv::BORDER_CONSTANT,cv::Scalar(0));
            for (int y=0;y<512;++y) for (int x=0;x<512;++x)
                image.at<uint8_t>(y,x)=cv::saturate_cast<uint8_t>(image.at<uint8_t>(y,x)+brightness+(noise>0?gaussian(rng):0));
            const std::string file="die-"+std::to_string(i)+".pgm", maskfile="mask-"+std::to_string(i)+".pgm";
            cv::imwrite((dir/file).string(),image); cv::imwrite((dir/maskfile).string(),mask);
            manifest << (i?",\n":"") << "{\"index\":" << i << ",\"image\":\"" << file << "\",\"mask\":\"" << maskfile
                << "\",\"kind\":\"" << name << "\",\"shiftX\":" << dx << ",\"shiftY\":" << dy
                << ",\"brightness\":" << brightness << ",\"widthPixels\":" << width << ",\"defectX\":" << defect.x
                << ",\"defectY\":" << defect.y << ",\"defectWidth\":" << defect.width << ",\"defectHeight\":" << defect.height << "}";
        }
        manifest << "]}";
        std::cout << "Generated " << count << " labeled frames, seed " << seed << "\n";
        return 0;
    } catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
}
