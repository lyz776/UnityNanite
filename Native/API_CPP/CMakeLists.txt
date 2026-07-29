# 可选：用 CMake + MSVC 生成 API_CPP.dll
# 在「本目录」打开终端执行（把路径改成你解压的 meshoptimizer 里 src 文件夹）：
#
#   cmake -B build -A x64 -DMESHOPT_SRC_DIR="../meshoptimizer/src"
#   cmake --build build --config Release
#
# 输出一般在 build/Release/API_CPP.dll，复制到 Unity Assets/Plugins/x86_64/

cmake_minimum_required(VERSION 3.15)
project(API_CPP LANGUAGES CXX)

if(NOT DEFINED MESHOPT_SRC_DIR OR MESHOPT_SRC_DIR STREQUAL "")
	message(FATAL_ERROR
		"请用 -DMESHOPT_SRC_DIR 指向 meshoptimizer 仓库里的 src 目录（内含 meshoptimizer.h 与 *.cpp），"
		"例如：-DMESHOPT_SRC_DIR=C:/Downloads/meshoptimizer-master/meshoptimizer-master/src")
endif()

if(NOT EXISTS "${MESHOPT_SRC_DIR}/meshoptimizer.h")
	message(FATAL_ERROR "MESHOPT_SRC_DIR 无效：找不到 meshoptimizer.h：${MESHOPT_SRC_DIR}/meshoptimizer.h")
endif()

file(GLOB MESHOPT_SOURCES CONFIGURE_DEPENDS "${MESHOPT_SRC_DIR}/*.cpp")
if(MESHOPT_SOURCES STREQUAL "")
	message(FATAL_ERROR "在 ${MESHOPT_SRC_DIR} 下未发现任何 .cpp；若你只看到 meshoptimizer.h，说明你下的不是完整源码或未进对目录。")
endif()

add_library(meshoptimizer_for_unity STATIC ${MESHOPT_SOURCES})
target_include_directories(meshoptimizer_for_unity PUBLIC "${MESHOPT_SRC_DIR}")
target_compile_features(meshoptimizer_for_unity PUBLIC cxx_std_17)

add_library(API_CPP SHARED api_cpp.cpp)
target_link_libraries(API_CPP PRIVATE meshoptimizer_for_unity)
target_compile_features(API_CPP PUBLIC cxx_std_17)
set_target_properties(API_CPP PROPERTIES OUTPUT_NAME "API_CPP")

if(MSVC)
	target_compile_options(meshoptimizer_for_unity PRIVATE /W3)
	target_compile_options(API_CPP PRIVATE /W3)
endif()
