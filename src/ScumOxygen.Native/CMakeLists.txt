cmake_minimum_required(VERSION 3.20)
project(ScumOxygen.Native VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# DLL для инжекции
add_library(ScumOxygen.Native SHARED
    src/dllmain.cpp
    src/hooks.cpp
    src/memory.cpp
    src/bridge.cpp
    src/ue4_sdk.cpp
)

target_include_directories(ScumOxygen.Native PRIVATE 
    include
    ${CMAKE_CURRENT_SOURCE_DIR}
)

# Линковка с Windows API
target_link_libraries(ScumOxygen.Native PRIVATE
    kernel32
    user32
    psapi
)

# Инжектор (консольное приложение)
add_executable(ScumOxygen.Injector injector.cpp)
target_link_libraries(ScumOxygen.Injector PRIVATE kernel32)

# Отключаем оптимизацию для отладки
target_compile_options(ScumOxygen.Native PRIVATE
    $<$<CONFIG:Debug>:/Od /Zi>
    $<$<CONFIG:Release>:/O2>
)

# Определяем экспорты
set_target_properties(ScumOxygen.Native PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
)
