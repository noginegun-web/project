cmake_minimum_required(VERSION 3.20)
project(ScumOxygenServerProxy LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(DOTNET_HOST_PACK_ROOT "C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Host.win-x64")
file(GLOB HOST_PACK_DIRS "${DOTNET_HOST_PACK_ROOT}/*/runtimes/win-x64/native")
if(NOT HOST_PACK_DIRS)
  message(FATAL_ERROR "Could not find .NET host pack at ${DOTNET_HOST_PACK_ROOT}")
endif()
list(SORT HOST_PACK_DIRS)
list(GET HOST_PACK_DIRS -1 HOST_PACK_DIR)
message(STATUS "Using host pack headers: ${HOST_PACK_DIR}")

add_library(scum_oidn_proxy SHARED version.cpp version.def)

target_include_directories(scum_oidn_proxy PRIVATE "${HOST_PACK_DIR}")

target_link_libraries(scum_oidn_proxy PRIVATE
  shlwapi
  version
)

set_target_properties(scum_oidn_proxy PROPERTIES
  OUTPUT_NAME "version"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
  LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/version.def\""
)
