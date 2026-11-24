cmake_minimum_required(VERSION 3.20)
project(SteinsGateInputFix LANGUAGES CXX)

if(NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "Build with MSVC and -A Win32")
endif()

add_library(SteinsGateInputFix SHARED SteinsGateInputFix.cpp)
target_compile_features(SteinsGateInputFix PRIVATE cxx_std_17)
target_compile_definitions(SteinsGateInputFix PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
target_compile_options(SteinsGateInputFix PRIVATE /W4)
target_link_libraries(SteinsGateInputFix PRIVATE user32 bcrypt)
set_property(TARGET SteinsGateInputFix PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
