cmake_minimum_required(VERSION 3.16)

find_package(Git REQUIRED)
execute_process(
    COMMAND "${GIT_EXECUTABLE}" -C "$ENV{IDF_PATH}" rev-parse HEAD
    OUTPUT_VARIABLE LDN_IDF_REVISION
    OUTPUT_STRIP_TRAILING_WHITESPACE
    RESULT_VARIABLE LDN_IDF_REVISION_RESULT
)
if(NOT LDN_IDF_REVISION_RESULT EQUAL 0 OR
   NOT LDN_IDF_REVISION STREQUAL "fff9895c82d744c7237be8847347bdd1b07c6643")
    message(FATAL_ERROR "The LDN probe requires the audited ESP-IDF v6.1 commit fff9895c82d744c7237be8847347bdd1b07c6643")
endif()

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(ldn_wifi_probe)
