ifeq ($(strip $(DEVKITARM)),)
$(error "Please set DEVKITARM in your environment. export DEVKITARM=<path to>devkitARM")
endif

include $(DEVKITARM)/base_rules

################################################################################

IPL_LOAD_ADDR := 0x40008000

TARGET    := switchdeck
BUILDDIR  := build
OUTPUTDIR := output
SOURCEDIR := src
HEKATE    := ../third_party/hekate
BDKDIR    := $(HEKATE)/bdk
HKGFXDIR  := $(HEKATE)/bootloader/gfx

VPATH  = $(SOURCEDIR) $(HKGFXDIR) $(HEKATE)/bootloader/libs/fatfs
VPATH += $(BDKDIR) $(dir $(wildcard $(BDKDIR)/*/)) $(dir $(wildcard $(BDKDIR)/*/*/))

# Payload.
OBJS = start exception_handlers main screen deck_link night heap gfx

# Matériel.
OBJS += bpmp ccplex clock di vic i2c irq timer \
		mc sdram minerva smmu \
		gpio pinmux pmc se tsec uart \
		fuse kfuse \
		bq24193 max17050 max7762x max77620-rtc regulator_5v \
		sdmmc sdmmc_driver sd emmc \
		touch hw_init

# Utilitaires.
OBJS += btn util sprintf

# Chargement de modules depuis la SD (libsys_minerva.bso de hekate).
OBJS += ianos elfload elfreloc_arm

# USB (usb_descriptors.c : copie modifiée dans src).
OBJS += usbd xusbd usb_descriptors

# Carte SD (FatFs).
OBJS += diskio ff ffunicode ffsystem

OBJS := $(addprefix $(BUILDDIR)/, $(addsuffix .o, $(OBJS)))

# Console texte de hekate (gfx.h) et configuration FatFs exigées par les en-têtes de la BDK.
GFX_INC   := '"gfx/gfx.h"'
FFCFG_INC := '"ffconf.h"'

################################################################################

CUSTOMDEFINES := -DIPL_LOAD_ADDR=$(IPL_LOAD_ADDR)
CUSTOMDEFINES += -DBDK_MALLOC_NO_DEFRAG
CUSTOMDEFINES += -DGFX_INC=$(GFX_INC) -DFFCFG_INC=$(FFCFG_INC)

WARNINGS := -Wall -Wsign-compare -Wtype-limits -Wno-array-bounds -Wno-stringop-overread -Wno-stringop-overflow

ARCH    := -march=armv4t -mtune=arm7tdmi -mthumb -mthumb-interwork $(WARNINGS)
INCS    := -I$(BDKDIR) -I$(HEKATE)/bootloader -I$(SOURCEDIR)
CFLAGS   = $(ARCH) -O2 -g -gdwarf-4 -nostdlib -ffunction-sections -fdata-sections -fomit-frame-pointer -fno-inline -std=gnu11 $(CUSTOMDEFINES)
LDFLAGS  = $(ARCH) -nostartfiles -lgcc -Wl,--nmagic,--gc-sections -Xlinker --defsym=IPL_LOAD_ADDR=$(IPL_LOAD_ADDR)

################################################################################

.PHONY: all clean

all: $(OUTPUTDIR)/$(TARGET).bin
	@echo "Taille : $$(wc -c < $<) octets"

clean:
	@rm -rf $(BUILDDIR) $(OUTPUTDIR)

$(OUTPUTDIR)/$(TARGET).bin: $(BUILDDIR)/$(TARGET).elf | $(OUTPUTDIR)
	@$(OBJCOPY) -S -O binary $< $@

$(BUILDDIR)/$(TARGET).elf: $(OBJS)
	@$(CC) $(LDFLAGS) -T $(SOURCEDIR)/link.ld $(OBJS) -o $@

$(BUILDDIR)/%.o: %.c | $(BUILDDIR)
	@echo "CC $<"
	@$(CC) $(CFLAGS) $(INCS) -MMD -MP -c $< -o $@

$(BUILDDIR)/%.o: %.S | $(BUILDDIR)
	@echo "AS $<"
	@$(CC) $(CFLAGS) -MMD -MP -c $< -o $@

$(BUILDDIR) $(OUTPUTDIR):
	@mkdir -p $@

-include $(OBJS:.o=.d)
