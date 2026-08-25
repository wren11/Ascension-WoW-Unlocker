cmake_minimum_required(VERSION 3.16)
project(AscensionBoot C)

add_executable(AscensionBoot AscensionBoot.c)
set_target_properties(AscensionBoot PROPERTIES
    OUTPUT_NAME AscensionBoot
    C_STANDARD 11
)
target_compile_definitions(AscensionBoot PRIVATE UNICODE _UNICODE)
target_link_libraries(AscensionBoot PRIVATE kernel32 user32 shell32)
if(MINGW)
    target_link_options(AscensionBoot PRIVATE -municode)
endif()
